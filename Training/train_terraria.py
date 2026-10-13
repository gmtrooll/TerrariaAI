import os
import argparse
import threading
import time
import warnings
from collections import Counter, deque
from dataclasses import dataclass, field
from typing import Optional, Dict, Any

import numpy as np
from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import BaseCallback, CheckpointCallback
from stable_baselines3.common.utils import set_random_seed
from stable_baselines3.common.vec_env import VecEnv, VecFrameStack, VecMonitor

# Load training configuration
from config_loader import load_training_config
from policy import policy_kwargs
from protocol import OBS_VERSION, TerrariaConnectionError
from reward_system import OUTCOMES
from terraria_env import TerrariaVecEnv

TRAINING_CONFIG = load_training_config()

# Paths are anchored to the Training folder, whatever the current directory is.
TRAINING_DIR = os.path.dirname(os.path.abspath(__file__))
MODELS_DIR = os.path.join(TRAINING_DIR, "models")
LOGS_DIR = os.path.join(TRAINING_DIR, "logs")
ZONE_STATS_PATH = os.path.join(LOGS_DIR, "zone_stats.json")


@dataclass
class TrainingConfig:
    """Configuration for training session (defaults come from training_config.json)"""
    num_bots: int = TRAINING_CONFIG['num_bots']
    batch_size: int = TRAINING_CONFIG['batch_size']  # PPO minibatch size (not the number of bots)
    learning_rate: float = TRAINING_CONFIG['learning_rate']
    entropy_coef: float = TRAINING_CONFIG['ent_coef']
    n_steps: int = TRAINING_CONFIG['n_steps']
    n_epochs: int = TRAINING_CONFIG['n_epochs']
    gamma: float = TRAINING_CONFIG['gamma']
    gae_lambda: float = TRAINING_CONFIG['gae_lambda']
    clip_range: float = TRAINING_CONFIG['clip_range']
    vf_coef: float = TRAINING_CONFIG['vf_coef']
    max_grad_norm: float = TRAINING_CONFIG['max_grad_norm']
    seed: Optional[int] = TRAINING_CONFIG['seed']
    frame_stack: int = TRAINING_CONFIG['frame_stack']
    total_timesteps: int = 5_000_000
    checkpoint_interval: int = 50_000
    device: str = "cuda"
    model_path: Optional[str] = None
    fresh_start: bool = False
    raw_config: Dict[str, Any] = field(default_factory=lambda: dict(TRAINING_CONFIG), repr=False)


# --------------------------------------------------------------------------- env + model factory

def make_env(cfg: TrainingConfig, **env_kwargs) -> VecEnv:
    """TerrariaVecEnv + VecMonitor (episode reward/length/success) + optional VecFrameStack."""
    raw = dict(cfg.raw_config, num_bots=cfg.num_bots, seed=cfg.seed)
    env_kwargs.setdefault("zone_stats_path", ZONE_STATS_PATH)
    env: VecEnv = TerrariaVecEnv(num_envs=cfg.num_bots, config=raw, **env_kwargs)
    env = VecMonitor(env, info_keywords=("is_success", "outcome"))
    if cfg.frame_stack and cfg.frame_stack > 1:
        env = VecFrameStack(env, n_stack=cfg.frame_stack, channels_order={"vision": "first", "stats": "last"})
    return env


def ppo_hyperparameters(cfg: TrainingConfig) -> Dict[str, Any]:
    return dict(
        batch_size=cfg.batch_size,
        learning_rate=cfg.learning_rate,
        ent_coef=cfg.entropy_coef,
        n_steps=cfg.n_steps,
        n_epochs=cfg.n_epochs,
        gamma=cfg.gamma,
        gae_lambda=cfg.gae_lambda,
        clip_range=cfg.clip_range,
        vf_coef=cfg.vf_coef,
        max_grad_norm=cfg.max_grad_norm,
    )


def build_model(env: VecEnv, cfg: TrainingConfig, model_path: Optional[str] = None,
                tensorboard_log: Optional[str] = LOGS_DIR, verbose: int = 1) -> PPO:
    """The one place PPO models are created or loaded."""
    if cfg.seed is not None:
        set_random_seed(cfg.seed)

    if model_path:
        if not os.path.exists(model_path + ".zip"):
            raise FileNotFoundError(f"model not found: {model_path}.zip")
        try:
            # Saved hyperparameters are kept; only device and log dir are taken from the config.
            model = PPO.load(model_path, env=env, device=cfg.device, tensorboard_log=tensorboard_log)
        except (ValueError, KeyError) as e:
            raise RuntimeError(
                f"Could not load {model_path}: {e}. Checkpoints trained with an older observation "
                f"layout (before OBS_VERSION {OBS_VERSION}) or a different frame_stack cannot be loaded; "
                f"start a new model.") from e
        if cfg.seed is not None:
            model.set_random_seed(cfg.seed)
        return model

    return PPO(
        "MultiInputPolicy",
        env,
        verbose=verbose,
        device=cfg.device,
        seed=cfg.seed,
        policy_kwargs=policy_kwargs(),
        tensorboard_log=tensorboard_log,
        **ppo_hyperparameters(cfg),
    )


# --------------------------------------------------------------------------- callbacks

class EpisodeOutcomeCallback(BaseCallback):
    """Logs win/death/timeout/aborted counts to TensorBoard (SB3 already logs rollout/success_rate)."""

    def __init__(self, verbose=0):
        super().__init__(verbose)
        self.rollout_counts = Counter()
        self.total_counts = Counter()

    def _on_step(self) -> bool:
        for info, done in zip(self.locals.get("infos", []), self.locals.get("dones", [])):
            if done and "outcome" in info:
                self.rollout_counts[info["outcome"]] += 1
                self.total_counts[info["outcome"]] += 1
        return True

    def _on_rollout_end(self) -> None:
        finished = sum(self.rollout_counts.values())
        for outcome in OUTCOMES:
            self.logger.record(f"episodes/{outcome}", self.rollout_counts[outcome])
            self.logger.record(f"episodes/total_{outcome}", self.total_counts[outcome])
        if finished:
            self.logger.record("episodes/rollout_success_rate", self.rollout_counts["win"] / finished)
        self.rollout_counts.clear()


class MetricsCallback(BaseCallback):
    """Custom callback to track live training metrics for the dashboard"""

    def __init__(self, metrics_dict: Dict[str, Any], verbose=0):
        super().__init__(verbose)
        self.metrics = metrics_dict
        self.episode_rewards = deque(maxlen=500)
        self.episode_lengths = deque(maxlen=100)
        self.episode_success = deque(maxlen=100)
        self.start_time = time.time()
        self.start_timesteps = None

    def _on_step(self) -> bool:
        if self.start_timesteps is None:
            self.start_timesteps = self.num_timesteps
            self.start_time = time.time()
            self.metrics["status"] = "training"
        elapsed = time.time() - self.start_time

        # Track episode info (added by VecMonitor when an episode ends)
        for info in self.locals.get("infos", []):
            if "episode" in info:
                self.episode_rewards.append(float(info["episode"]["r"]))
                self.episode_lengths.append(int(info["episode"]["l"]))
                if "is_success" in info:
                    self.episode_success.append(float(info["is_success"]))

        # Replace values (never mutate shared containers in place): the dashboard thread reads them.
        self.metrics["timesteps"] = self.num_timesteps
        self.metrics["elapsed_time"] = elapsed
        if elapsed > 0:
            self.metrics["fps"] = (self.num_timesteps - self.start_timesteps) / elapsed
        if self.episode_rewards:
            self.metrics["mean_reward"] = float(np.mean(list(self.episode_rewards)[-100:]))
            self.metrics["reward_history"] = list(self.episode_rewards)
        if self.episode_lengths:
            self.metrics["mean_episode_length"] = float(np.mean(self.episode_lengths))
        if self.episode_success:
            self.metrics["success_rate"] = float(np.mean(self.episode_success))
        return True


class StopCallback(BaseCallback):
    def __init__(self, manager: "TrainingManager"):
        super().__init__()
        self.manager = manager

    def _on_step(self):
        return not self.manager.should_stop


def make_callbacks(cfg: TrainingConfig, extra=()):
    checkpoint_callback = CheckpointCallback(
        save_freq=max(1, cfg.checkpoint_interval // cfg.num_bots),  # counted in vec-env steps
        save_path=MODELS_DIR,
        name_prefix="terraria_bot",
        save_replay_buffer=False,
        save_vecnormalize=False,
        verbose=1
    )
    return [checkpoint_callback, EpisodeOutcomeCallback(), *extra]


# --------------------------------------------------------------------------- dashboard backend

class TrainingManager:
    """Manages the RL training process with external control (used by dashboard.py)"""

    def __init__(self, config: TrainingConfig):
        self.config = config
        self.model: Optional[PPO] = None
        self.env: Optional[VecEnv] = None
        self.is_training = False
        self.should_stop = False
        self.training_thread: Optional[threading.Thread] = None

        # Live metrics shared with the dashboard thread. All keys exist up front and values are
        # only ever replaced, so get_metrics() can copy the dict from another thread safely.
        self.metrics: Dict[str, Any] = {
            "timesteps": 0,
            "total_timesteps": config.total_timesteps,
            "mean_reward": 0.0,
            "mean_episode_length": 0.0,
            "success_rate": 0.0,
            "fps": 0.0,
            "elapsed_time": 0.0,
            "reward_history": [],
            "status": "idle",
            "model_loaded": False,
            "model_path": None,
        }

        os.makedirs(MODELS_DIR, exist_ok=True)

    def get_available_models(self) -> list:
        """List all saved models in the models directory"""
        if not os.path.exists(MODELS_DIR):
            return []
        return sorted((f[:-4] for f in os.listdir(MODELS_DIR) if f.endswith(".zip")), reverse=True)

    def load_model(self, model_name: str) -> bool:
        """Select a saved model (loading happens at training start, once Terraria is connected)"""
        model_path = os.path.join(MODELS_DIR, model_name)
        if not os.path.exists(model_path + ".zip"):
            self.metrics["status"] = f"Model not found: {model_name}"
            return False
        self.config.model_path = model_path
        self.metrics["model_loaded"] = True
        self.metrics["model_path"] = model_name
        self.metrics["status"] = f"Selected: {model_name} (will load on start)"
        return True

    def create_new_model(self) -> bool:
        """Select a fresh model (created at training start, so the UI never blocks on the socket)"""
        self.config.model_path = None
        self.model = None
        self.metrics["model_loaded"] = True
        self.metrics["model_path"] = "new_model"
        self.metrics["status"] = "New model (will be created on start)"
        self.metrics["timesteps"] = 0
        return True

    def save_model(self, name: str = "terraria_bot_latest") -> bool:
        """Save the current model"""
        if self.model is None:
            return False
        try:
            self.model.save(os.path.join(MODELS_DIR, name))
            self.metrics["status"] = f"Saved: {name}"
            return True
        except Exception as e:
            self.metrics["status"] = f"Save error: {str(e)}"
            return False

    def update_config(self, **kwargs):
        """Update training configuration"""
        for key, value in kwargs.items():
            if hasattr(self.config, key):
                setattr(self.config, key, value)
        self.metrics["total_timesteps"] = self.config.total_timesteps

    def start_training(self):
        """Start training in a background thread"""
        if self.is_training:
            return

        self.should_stop = False
        self.is_training = True
        self.metrics["status"] = "starting..."

        # All blocking operations happen in the background thread
        self.training_thread = threading.Thread(target=self._training_loop, daemon=True)
        self.training_thread.start()

    def stop_training(self):
        """Stop the training loop"""
        self.should_stop = True
        self.metrics["status"] = "stopping..."

    def _training_loop(self):
        """Internal training loop running in background thread"""
        save_on_exit = False
        try:
            self.metrics["status"] = "waiting for Terraria connection..."
            self.env = make_env(self.config)
            self.metrics["status"] = "loading model..." if self.config.model_path else "creating model..."
            self.model = build_model(self.env, self.config, self.config.model_path)
            self.metrics["timesteps"] = self.model.num_timesteps
            self.metrics["model_loaded"] = True
            if not self.config.model_path:
                self.metrics["model_path"] = "new_model"

            self.metrics["status"] = "waiting for bots..."
            save_on_exit = True
            self.model.learn(
                total_timesteps=self.config.total_timesteps,
                callback=make_callbacks(self.config, [MetricsCallback(self.metrics), StopCallback(self)]),
                reset_num_timesteps=False
            )

            if self.should_stop:
                self.save_model("terraria_bot_latest")
                self.metrics["status"] = "stopped (saved)"
            else:
                self.save_model("terraria_bot_final")
                self.metrics["status"] = "completed"
            save_on_exit = False

        except TerrariaConnectionError as e:
            self.metrics["status"] = f"Error: {e} (training stopped)"
        except Exception as e:
            self.metrics["status"] = f"Error: {str(e)}"
        finally:
            if save_on_exit and self.model is not None:
                self.save_model("terraria_bot_latest")
            self.is_training = False
            # Gracefully close the connection so Terraria doesn't wait forever
            if self.env:
                try:
                    self.env.close()
                except Exception:
                    pass
                self.env = None
            self.model = None

    def get_metrics(self) -> Dict[str, Any]:
        """Get current training metrics"""
        return self.metrics.copy()

    def cleanup(self):
        """Clean up resources"""
        self.stop_training()
        if self.env:
            self.env.close()


# --------------------------------------------------------------------------- CLI

def parse_args(argv=None):
    parser = argparse.ArgumentParser(description='Train Terraria RL Bot')
    parser.add_argument('--load-model', type=str, default=None,
                       help='Path to model to load (without .zip extension)')
    parser.add_argument('--fresh-start', action='store_true',
                       help='Start training from scratch, ignore existing models')
    parser.add_argument('--checkpoint-interval', type=int, default=50000,
                       help='Save checkpoint every N timesteps (default: 50000)')
    parser.add_argument('--total-timesteps', type=int, default=5000000,
                       help='Total training timesteps (default: 5000000)')
    parser.add_argument('--num-bots', type=int, default=None,
                       help=f"Number of bots / parallel envs; must match the mod (default: num_bots from "
                            f"training_config.json = {TRAINING_CONFIG['num_bots']})")
    parser.add_argument('--batch-size', type=int, default=None, dest='legacy_batch_size',
                       help=argparse.SUPPRESS)  # old name of --num-bots
    parser.add_argument('--seed', type=int, default=None, help='Random seed (default: seed from config)')
    parser.add_argument('--device', type=str, default='cuda',
                       help='Device to use: cuda or cpu (default: cuda)')
    parser.add_argument('--dashboard', action='store_true',
                       help='Launch the Streamlit dashboard instead of CLI training')
    args = parser.parse_args(argv)
    if args.legacy_batch_size is not None:
        warnings.warn("--batch-size meant the number of bots; use --num-bots instead")
        if args.num_bots is None:
            args.num_bots = args.legacy_batch_size
    return args


def main(argv=None):
    args = parse_args(argv)

    # Dashboard mode
    if args.dashboard:
        import subprocess
        import sys
        dashboard_path = os.path.join(TRAINING_DIR, "dashboard.py")
        subprocess.run([sys.executable, "-m", "streamlit", "run", dashboard_path], cwd=TRAINING_DIR)
        return

    cfg = TrainingConfig(
        num_bots=args.num_bots if args.num_bots is not None else TRAINING_CONFIG['num_bots'],
        seed=args.seed if args.seed is not None else TRAINING_CONFIG['seed'],
        device=args.device,
        total_timesteps=args.total_timesteps,
        checkpoint_interval=args.checkpoint_interval,
    )
    os.makedirs(MODELS_DIR, exist_ok=True)

    print(f"\n{'='*60}")
    print(f"Terraria RL Training Configuration")
    print(f"{'='*60}")
    print(f"Bots: {cfg.num_bots} | Device: {cfg.device} | Seed: {cfg.seed} | Frame stack: {cfg.frame_stack}")
    print(f"Checkpoint Interval: {cfg.checkpoint_interval:,} steps | Total Timesteps: {cfg.total_timesteps:,}")
    print(f"Models: {MODELS_DIR}")
    print(f"{'='*60}\n")

    latest_model = os.path.join(MODELS_DIR, "terraria_bot_latest")
    if args.fresh_start:
        model_path = None
    elif args.load_model:
        model_path = args.load_model
    elif os.path.exists(latest_model + ".zip"):
        model_path = latest_model
    else:
        model_path = None

    env = make_env(cfg)
    model = None
    try:
        model = build_model(env, cfg, model_path)
        print(f"{'Loaded ' + model_path if model_path else 'Created new model'} "
              f"({model.num_timesteps:,} timesteps trained so far)")
        print(f"Checkpoints: {MODELS_DIR}/terraria_bot_<step>_steps.zip. Press Ctrl+C to stop and save\n")
        model.learn(
            total_timesteps=cfg.total_timesteps,
            callback=make_callbacks(cfg),
            reset_num_timesteps=False  # Continue counting from loaded model
        )
        final_path = os.path.join(MODELS_DIR, "terraria_bot_final")
        model.save(final_path)
        print(f"\nTraining completed! Final model saved to: {final_path}")
    except KeyboardInterrupt:
        if model is None:
            raise
        model.save(latest_model)
        print(f"\nTraining interrupted! Model saved to: {latest_model}")
        print(f"Resume training with: python train_terraria.py --load-model {latest_model}")
    except TerrariaConnectionError as e:
        if model is not None:
            model.save(latest_model)
        raise SystemExit(f"\nStopped: {e}. Model saved to {latest_model}; the transitions of the "
                         f"unfinished rollout were discarded.")
    finally:
        env.close()


if __name__ == "__main__":
    main()
