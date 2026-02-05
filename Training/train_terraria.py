import socket
import struct
import numpy as np
import time
import gymnasium as gym
from gymnasium import spaces
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import VecEnv
from stable_baselines3.common.callbacks import BaseCallback, CheckpointCallback
import os
import argparse
import threading
from dataclasses import dataclass, field
from typing import Optional, Dict, Any, Callable

# Load training configuration
from config_loader import load_training_config
from reward_system import RewardSystem  # Import new system

TRAINING_CONFIG = load_training_config()


@dataclass
class TrainingConfig:
    """Configuration for training session"""
    num_bots: int = TRAINING_CONFIG.get('num_bots', 16)
    batch_size: int = TRAINING_CONFIG.get('batch_size', 512)
    learning_rate: float = TRAINING_CONFIG.get('learning_rate', 0.0002)
    entropy_coef: float = TRAINING_CONFIG.get('ent_coef', 0.01)
    n_steps: int = TRAINING_CONFIG.get('n_steps', 512)
    n_epochs: int = TRAINING_CONFIG.get('n_epochs', 4)
    gamma: float = TRAINING_CONFIG.get('gamma', 0.99)
    gae_lambda: float = TRAINING_CONFIG.get('gae_lambda', 0.95)
    clip_range: float = TRAINING_CONFIG.get('clip_range', 0.2)
    vf_coef: float = TRAINING_CONFIG.get('vf_coef', 0.5)
    max_grad_norm: float = TRAINING_CONFIG.get('max_grad_norm', 0.5)
    total_timesteps: int = 5_000_000
    checkpoint_interval: int = 50_000
    device: str = "cuda"
    model_path: Optional[str] = None
    fresh_start: bool = False


class TerrariaVecEnv(VecEnv):
    def __init__(self, num_envs=None, zone_database_path: Optional[str] = None):
        # Load config if num_envs not specified
        if num_envs is None:
            num_envs = TRAINING_CONFIG.get('num_bots', 16)
        
        # CONFIGURATION (hardcoded values that require model retraining)
        self.GRID_SIZE = 40  # Cannot be changed without retraining model
        self.STATS_SIZE = 46  # Cannot be changed without code changes
        self.GRID_INPUTS = self.GRID_SIZE * self.GRID_SIZE
        self.NUM_ENVS = num_envs
        
        # PRE-CALCULATE PACKET SIZES
        self.STATS_BYTES = self.NUM_ENVS * self.STATS_SIZE * 4
        self.GRID_BYTES = self.NUM_ENVS * self.GRID_INPUTS * 4
        self.EXTRA_BYTES = self.NUM_ENVS * 5 
        self.TOTAL_PACKET_SIZE = self.STATS_BYTES + self.GRID_BYTES + self.EXTRA_BYTES
        
        # Buffer will automatically size based on bot count
        self.recv_buffer = bytearray(self.TOTAL_PACKET_SIZE)

        # Performance Tracking
        self.frame_count = 0
        self.last_time = time.time()
        
        # --- ZONE MANAGEMENT ---
        from zone_manager import ZoneManager, get_default_database_path
        db_path = zone_database_path or get_default_database_path()
        self.zone_manager = ZoneManager(db_path)
        
        # Apply zone config settings
        self.zone_manager.min_difficulty = TRAINING_CONFIG.get('zone_min_difficulty', 1)
        self.zone_manager.max_difficulty = TRAINING_CONFIG.get('zone_max_difficulty', 10)
        self.zone_manager.use_performance_sampling = TRAINING_CONFIG.get('use_performance_sampling', True)
        
        self.pending_zone_assignments: Dict[int, tuple] = {}  # bot_id -> (spawn_x, spawn_y, target_x, target_y)
        self.use_zones = len(self.zone_manager.zones) > 0
        if self.use_zones:
            print(f"[ZoneEnv] Loaded {len(self.zone_manager.zones)} training zones")
        else:
            print("[ZoneEnv] No zones loaded - using legacy target marker mode")
        # -----------------------

        # Define Spaces
        observation_space = spaces.Dict({
            "vision": spaces.Box(low=0, high=1, shape=(1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32),
            "stats":  spaces.Box(low=-np.inf, high=np.inf, shape=(self.STATS_SIZE,), dtype=np.float32)
        })
        action_space = spaces.MultiBinary(3)

        super().__init__(num_envs, observation_space, action_space)
        
        # Networking - use port from config
        port = TRAINING_CONFIG.get('port', 5555)
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1) 
        self.server.bind(('127.0.0.1', port))
        self.server.listen(1)
        self.conn = None
        self.wait_for_connection()
        
        self.conn = None
        self.wait_for_connection()
        
        # Initialize Reward System
        self.reward_system = RewardSystem()
        
        # Assign initial zones to all bots
        if self.use_zones:
            self._assign_zones_to_all_bots()

    def wait_for_connection(self):
        print(f"Waiting for Terraria ({self.NUM_ENVS} bots)...")
        self.conn, self.addr = self.server.accept()
        print(f"Connected to {self.addr}")

    # --- REQUIRED ABSTRACT METHODS ---
    def env_is_wrapped(self, wrapper_class, indices=None): return [False] * self.NUM_ENVS
    def env_method(self, method_name, *method_args, indices=None, **method_kwargs): return [None] * self.NUM_ENVS
    def get_attr(self, attr_name, indices=None): return [None] * self.NUM_ENVS
    def set_attr(self, attr_name, value, indices=None): pass
    # ---------------------------------

    # --- ZONE MANAGEMENT METHODS ---
    def _assign_zones_to_all_bots(self):
        """Assign a zone to each bot at startup"""
        print(f"[ZoneEnv] ========== ASSIGNING ZONES TO ALL {self.NUM_ENVS} BOTS ==========")
        print(f"[ZoneEnv] Pending assignments before: {list(self.pending_zone_assignments.keys())}")
        for i in range(self.NUM_ENVS):
            self._assign_new_zone(i)
        print(f"[ZoneEnv] Pending assignments after: {list(self.pending_zone_assignments.keys())}")
        print(f"[ZoneEnv] ========== ZONE ASSIGNMENT COMPLETE ==========")
    
    def _assign_new_zone(self, bot_id: int):
        """Assign a new zone to a specific bot"""
        if not self.use_zones:
            return
        
        # Check if bot already has a pending assignment
        if bot_id in self.pending_zone_assignments:
            print(f"[ZoneEnv] WARNING: Bot {bot_id} already has pending zone assignment - skipping duplicate")
            return
        
        zone = self.zone_manager.sample_zone()
        if zone:
            self.zone_manager.assign_zone_to_bot(bot_id, zone)
            loc = zone.original_location
            self.pending_zone_assignments[bot_id] = (loc.spawn_x, loc.spawn_y, loc.target_x, loc.target_y)
            print(f"[ZoneEnv] Bot {bot_id} -> Zone '{zone.zone_id}' (spawn: {loc.spawn_x:.0f}, {loc.spawn_y:.0f})")
        else:
            print(f"[ZoneEnv] WARNING: No zone available for bot {bot_id}")
    # -------------------------------

    def step_async(self, actions):
        if self.conn is None: return
        try:
            # 'actions' is a numpy array of shape (16, 3) -> [[0,1,0], [1,1,1]...]
            # We pack it into a single integer bitmask for C#
            for i, act in enumerate(actions):
                flags = 0
                # Check indices: 0=Left, 1=Right, 2=Jump
                if act[0] > 0.5: flags |= 1 
                if act[1] > 0.5: flags |= 2 
                if act[2] > 0.5: flags |= 4 
                
                # Check if this bot needs a zone assignment (teleport)
                if i in self.pending_zone_assignments:
                    spawn_x, spawn_y, target_x, target_y = self.pending_zone_assignments.pop(i)
                    # Send zone command: 1000000 + action, followed by 4 floats
                    command = 1000000 + flags
                    print(f"[ZoneEnv] >>> Consuming pending zone for bot {i}, sending TELEPORT: spawn=({spawn_x:.0f}, {spawn_y:.0f}), target=({target_x:.0f}, {target_y:.0f})")
                    self.conn.sendall(struct.pack('iffff', command, spawn_x, spawn_y, target_x, target_y))
                else:
                    # Normal action
                    self.conn.sendall(struct.pack('i', flags))
                    
        except (BrokenPipeError, OSError):
            print("Connection Lost during Send.")
            self.conn = None

    def step_wait(self):
        if self.conn is None: return self._get_dummy_batch()

        try:
            self.recv_into_buffer(self.recv_buffer)

            # --- DEBUG FPS ---
            self.frame_count += 1
            if self.frame_count % 100 == 0:
                now = time.time()
                fps = (100 * self.NUM_ENVS) / (now - self.last_time)
                # print(f"Processing Speed: {fps:.2f} frames/sec") 
                self.last_time = now
            # -----------------

            offset = 0

            # 1. Stats
            stats_flat = np.frombuffer(self.recv_buffer, dtype=np.float32, count=self.NUM_ENVS * self.STATS_SIZE, offset=offset)
            stats_batch = stats_flat.reshape((self.NUM_ENVS, self.STATS_SIZE)).copy()
            offset += self.STATS_BYTES

            # 2. Grids
            grid_flat = np.frombuffer(self.recv_buffer, dtype=np.float32, count=self.NUM_ENVS * self.GRID_INPUTS, offset=offset)
            grid_batch = grid_flat.reshape((self.NUM_ENVS, 1, self.GRID_SIZE, self.GRID_SIZE)).copy()
            offset += self.GRID_BYTES

            # 3. Rewards & Dones
            rewards = []
            dones = []
            infos = [{} for _ in range(self.NUM_ENVS)]
            
            for i in range(self.NUM_ENVS):
                agent_offset = offset + (i * 5)
                # Unpack RAW DISTANCE (float) + FLAGS (byte) instead of Reward + Done
                raw_dist = struct.unpack_from('f', self.recv_buffer, agent_offset)[0]
                flags_byte = struct.unpack_from('B', self.recv_buffer, agent_offset + 4)[0] # 'B' = unsigned char
                
                # Calculate Reward using Python System
                # PASS GRID AND STATS for Geodesic Calculation
                # grid_batch[i] is (1, 40, 40), stats_batch[i] is (46,)
                r, d, info = self.reward_system.calculate_reward(
                    i, 
                    raw_dist, 
                    flags_byte, 
                    grid_batch[i], 
                    stats_batch[i]
                )
                
                rewards.append(r)
                dones.append(d)
                infos[i].update(info)
                
                if d:
                    infos[i]["terminal_observation"] = {
                        "vision": grid_batch[i],
                        "stats": stats_batch[i]
                    }
                    
                    # --- ZONE EPISODE TRACKING ---
                    if self.use_zones:
                        # Determine reason from info or flags
                        # Helper for logging
                        reason = info.get("outcome", "UNKNOWN").upper()
                        
                        print(f"[ZoneEnv] Bot {i} episode DONE ({reason}, reward={r:.1f})")
                        
                        success = (reason == "WIN")
                        died = (reason == "DIED")
                        self.zone_manager.record_episode_result(i, success, died)
                        # MOVED ASSIGNMENT: Defer to after loop to avoid race conditions
                    # -----------------------------

            # --- DEFERRED ZONE ASSIGNMENT ---
            # Assign new zones for all completed episodes AFTER processing all observations
            if self.use_zones:
                for i in range(self.NUM_ENVS):
                    if dones[i]:
                        self._assign_new_zone(i)
            # --------------------------------

            obs = {
                "vision": grid_batch,
                "stats": stats_batch
            }
            
            return obs, np.array(rewards), np.array(dones), infos

        except (BrokenPipeError, struct.error, AttributeError, ValueError) as e:
            print(f"Connection Lost or Data Error: {e}")
            self.conn = None
            return self._get_dummy_batch()

    def recv_into_buffer(self, buf):
        view = memoryview(buf)
        pos = 0
        while pos < len(buf):
            read = self.conn.recv_into(view[pos:])
            if read == 0: raise BrokenPipeError()
            pos += read

    def reset(self):
        if self.conn is None: self.wait_for_connection()
        obs, _, _, _ = self._get_dummy_batch()
        return obs

    def close(self):
        """Close all socket connections gracefully"""
        try:
            if self.conn:
                self.conn.shutdown(socket.SHUT_RDWR)
                self.conn.close()
        except:
            pass
        try:
            if self.server:
                self.server.close()
        except:
            pass
        self.conn = None

    def _get_dummy_batch(self):
        obs = {
            "vision": np.zeros((self.NUM_ENVS, 1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32),
            "stats": np.zeros((self.NUM_ENVS, self.STATS_SIZE), dtype=np.float32)
        }
        return obs, np.zeros(self.NUM_ENVS), np.zeros(self.NUM_ENVS, dtype=bool), [{}] * self.NUM_ENVS


class MetricsCallback(BaseCallback):
    """Custom callback to track live training metrics for the dashboard"""
    
    def __init__(self, metrics_dict: Dict[str, Any], verbose=0):
        super().__init__(verbose)
        self.metrics = metrics_dict
        self.episode_rewards = []
        self.episode_lengths = []
        self.start_time = time.time()
        self.last_check_time = time.time()
        self.last_check_timesteps = 0
        self.real_training_started = False
    
    def _on_step(self) -> bool:
        now = time.time()
        elapsed = now - self.start_time
        check_elapsed = now - self.last_check_time
        
        # Check FPS every second to see if real training is happening
        if check_elapsed >= 1.0:
            steps_since_check = self.num_timesteps - self.last_check_timesteps
            recent_fps = steps_since_check / check_elapsed
            
            # If we're getting more than 10 steps/sec, real data is flowing
            if recent_fps > 10 and not self.real_training_started:
                self.real_training_started = True
                self.start_time = time.time()  # Reset timer 
                self.metrics["status"] = "training"
            
            self.last_check_time = now
            self.last_check_timesteps = self.num_timesteps
        
        # Update basic metrics
        self.metrics["timesteps"] = self.num_timesteps
        self.metrics["elapsed_time"] = elapsed if self.real_training_started else 0
        
        # Calculate FPS
        if elapsed > 0:
            self.metrics["fps"] = self.num_timesteps / elapsed
        
        # Track episode info from infos
        for info in self.locals.get("infos", []):
            if "episode" in info:
                self.episode_rewards.append(info["episode"]["r"])
                self.episode_lengths.append(info["episode"]["l"])
        
        # Calculate rolling averages (last 100 episodes)
        if self.episode_rewards:
            recent_rewards = self.episode_rewards[-100:]
            self.metrics["mean_reward"] = np.mean(recent_rewards)
            self.metrics["reward_history"] = list(self.episode_rewards[-500:])  # Keep last 500
        
        if self.episode_lengths:
            recent_lengths = self.episode_lengths[-100:]
            self.metrics["mean_episode_length"] = np.mean(recent_lengths)
        
        return True


class TrainingManager:
    """Manages the RL training process with external control"""
    
    def __init__(self, config: TrainingConfig):
        self.config = config
        self.model: Optional[PPO] = None
        self.env: Optional[TerrariaVecEnv] = None
        self.is_training = False
        self.should_stop = False
        self.training_thread: Optional[threading.Thread] = None
        
        # Live metrics shared with dashboard
        self.metrics: Dict[str, Any] = {
            "timesteps": 0,
            "total_timesteps": config.total_timesteps,
            "mean_reward": 0.0,
            "mean_episode_length": 0.0,
            "fps": 0.0,
            "elapsed_time": 0.0,
            "reward_history": [],
            "status": "idle",
            "model_loaded": False,
            "model_path": None,
        }
        
        os.makedirs("models", exist_ok=True)
    
    def get_available_models(self) -> list:
        """List all saved models in the models directory"""
        models = []
        if os.path.exists("models"):
            for f in os.listdir("models"):
                if f.endswith(".zip"):
                    models.append(f.replace(".zip", ""))
        return sorted(models, reverse=True)
    
    def load_model(self, model_name: str) -> bool:
        """Load a saved model (deferred - actual loading happens at training start)"""
        try:
            model_path = f"models/{model_name}"
            if not os.path.exists(model_path + ".zip"):
                self.metrics["status"] = f"Model not found: {model_name}"
                return False
            
            # Store path for deferred loading (don't connect yet)
            self.config.model_path = model_path
            self.metrics["model_loaded"] = True
            self.metrics["model_path"] = model_name
            self.metrics["status"] = f"Selected: {model_name} (will load on start)"
            return True
        except Exception as e:
            self.metrics["status"] = f"Load error: {str(e)}"
            return False
    
    def create_new_model(self) -> bool:
        """Create a fresh model"""
        try:
            if self.env is None:
                self.env = TerrariaVecEnv(num_envs=self.config.num_bots)
            
            self.model = PPO(
                "MultiInputPolicy", 
                self.env, 
                verbose=1,
                device=self.config.device,
                batch_size=self.config.batch_size,
                learning_rate=self.config.learning_rate,
                ent_coef=self.config.entropy_coef,
                n_steps=self.config.n_steps,
                n_epochs=self.config.n_epochs,
                gamma=self.config.gamma,
                gae_lambda=self.config.gae_lambda,
                clip_range=self.config.clip_range,
                vf_coef=self.config.vf_coef,
                max_grad_norm=self.config.max_grad_norm,
                policy_kwargs=dict(normalize_images=False),
                tensorboard_log="./logs/"
            )
            self.metrics["model_loaded"] = True
            self.metrics["model_path"] = "new_model"
            self.metrics["status"] = "New model created"
            self.metrics["timesteps"] = 0
            return True
        except Exception as e:
            self.metrics["status"] = f"Create error: {str(e)}"
            return False
    
    def save_model(self, name: str = "terraria_bot_latest") -> bool:
        """Save the current model"""
        if self.model is None:
            return False
        try:
            path = f"models/{name}"
            self.model.save(path)
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
        try:
            # Create env and model in background thread (blocking operations)
            if self.model is None:
                self.metrics["status"] = "waiting for Terraria connection..."
                
                try:
                    if self.env is None:
                        self.env = TerrariaVecEnv(num_envs=self.config.num_bots)
                    
                    # Check if we should load an existing model
                    if self.config.model_path and os.path.exists(self.config.model_path + ".zip"):
                        self.metrics["status"] = f"loading model..."
                        self.model = PPO.load(self.config.model_path, env=self.env, device=self.config.device)
                        self.model.policy.to(self.config.device)
                        self.metrics["timesteps"] = self.model.num_timesteps
                    else:
                        # Create new model
                        self.metrics["status"] = "creating model..."
                        self.model = PPO(
                            "MultiInputPolicy", 
                            self.env, 
                            verbose=1,
                            device=self.config.device,
                            batch_size=self.config.batch_size,
                            learning_rate=self.config.learning_rate,
                            ent_coef=self.config.entropy_coef,
                            n_steps=self.config.n_steps,
                            n_epochs=self.config.n_epochs,
                            policy_kwargs=dict(normalize_images=False),
                            tensorboard_log="./logs/"
                        )
                        self.metrics["model_path"] = "new_model"
                    
                    self.metrics["model_loaded"] = True
                except Exception as e:
                    self.metrics["status"] = f"Error: {str(e)}"
                    self.is_training = False
                    return
            
            # Safety check
            if self.model is None:
                self.metrics["status"] = "Error: No model loaded"
                self.is_training = False
                return
            
            self.metrics["status"] = "waiting for bots..."
            self.model.policy.train()
            
            # Setup callbacks
            metrics_callback = MetricsCallback(self.metrics)
            checkpoint_callback = CheckpointCallback(
                save_freq=self.config.checkpoint_interval // self.config.num_bots,
                save_path="./models/",
                name_prefix="terraria_bot",
                save_replay_buffer=False,
                save_vecnormalize=False,
                verbose=1
            )
            
            # Custom stopping callback
            class StopCallback(BaseCallback):
                def __init__(self, manager):
                    super().__init__()
                    self.manager = manager
                
                def _on_step(self):
                    return not self.manager.should_stop
            
            stop_callback = StopCallback(self)
            
            self.model.learn(
                total_timesteps=self.config.total_timesteps,
                callback=[metrics_callback, checkpoint_callback, stop_callback],
                reset_num_timesteps=False
            )
            
            # Save on completion
            self.save_model("terraria_bot_final")
            self.metrics["status"] = "completed"
            
        except Exception as e:
            self.metrics["status"] = f"Error: {str(e)}"
        finally:
            self.is_training = False
            if self.should_stop:
                self.save_model("terraria_bot_latest")
                self.metrics["status"] = "stopped (saved)"
            # Gracefully close the connection so Terraria doesn't crash
            if self.env:
                try:
                    self.env.close()
                except:
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


# --- CLI BACKWARD COMPATIBILITY ---

def create_model_directory():
    """Create models directory if it doesn't exist"""
    os.makedirs("models", exist_ok=True)
    print("Model directory: ./models/")

def load_or_create_model(env, model_path=None, device="cuda"):
    """Load existing model or create new one"""
    if model_path and os.path.exists(model_path + ".zip"):
        print(f"Loading existing model from: {model_path}")
        try:
            model = PPO.load(model_path, env=env, device=device)
            print("Model loaded successfully!")
            
            # Diagnostic output
            print(f"  - Device: {model.device}")
            print(f"  - Learning rate: {model.learning_rate}")
            print(f"  - Number of timesteps trained: {model.num_timesteps}")
            
            # Ensure model is on correct device
            model.policy.to(device)
            print(f"  - Policy moved to: {device}")
            
        except Exception as e:
            print(f"ERROR loading model: {e}")
            print("Creating new model instead...")
            model = None
    else:
        model = None
    
    if model is None:
        print("Creating new model from scratch...")
        model = PPO(
            "MultiInputPolicy", 
            env, 
            verbose=1,
            device=device,
            batch_size=2048,
            learning_rate=0.0002,
            ent_coef=0.01,
            n_steps=512,
            n_epochs=4,
            policy_kwargs=dict(normalize_images=False),
            tensorboard_log="./logs/"
        )
        print("New model created!")
    return model


if __name__ == "__main__":
    # Parse command-line arguments
    parser = argparse.ArgumentParser(description='Train Terraria RL Bot')
    parser.add_argument('--load-model', type=str, default=None,
                       help='Path to model to load (without .zip extension)')
    parser.add_argument('--fresh-start', action='store_true',
                       help='Start training from scratch, ignore existing models')
    parser.add_argument('--checkpoint-interval', type=int, default=50000,
                       help='Save checkpoint every N timesteps (default: 50000)')
    parser.add_argument('--total-timesteps', type=int, default=5000000,
                       help='Total training timesteps (default: 5000000)')
    parser.add_argument('--batch-size', type=int, default=16,
                       help='Number of bots (default: 16)')
    parser.add_argument('--device', type=str, default='cuda',
                       help='Device to use: cuda or cpu (default: cuda)')
    parser.add_argument('--dashboard', action='store_true',
                       help='Launch the Streamlit dashboard instead of CLI training')
    
    args = parser.parse_args()
    
    # Dashboard mode
    if args.dashboard:
        import subprocess
        import sys
        script_dir = os.path.dirname(os.path.abspath(__file__))
        dashboard_path = os.path.join(script_dir, "dashboard.py")
        subprocess.run([sys.executable, "-m", "streamlit", "run", dashboard_path])
        sys.exit(0)
    
    # CLI mode (original behavior)
    BATCH_SIZE = args.batch_size
    create_model_directory()
    
    print(f"\n{'='*60}")
    print(f"Terraria RL Training Configuration")
    print(f"{'='*60}")
    print(f"Batch Size: {BATCH_SIZE} bots")
    print(f"Device: {args.device}")
    print(f"Checkpoint Interval: {args.checkpoint_interval:,} steps")
    print(f"Total Timesteps: {args.total_timesteps:,}")
    print(f"{'='*60}\n")
    
    env = TerrariaVecEnv(num_envs=BATCH_SIZE)
    
    # Load or create model
    if args.fresh_start:
        print("Fresh start requested - creating new model\n")
        model = load_or_create_model(env, model_path=None, device=args.device)
    else:
        # Try to load latest model
        latest_model = "models/terraria_bot_latest"
        if args.load_model:
            model = load_or_create_model(env, model_path=args.load_model, device=args.device)
        elif os.path.exists(latest_model + ".zip"):
            model = load_or_create_model(env, model_path=latest_model, device=args.device)
        else:
            model = load_or_create_model(env, model_path=None, device=args.device)
    
    # Ensure model is in training mode (not evaluation)
    model.policy.train()
    print(f"Model set to training mode\n")
    
    # Setup checkpointing
    checkpoint_callback = CheckpointCallback(
        save_freq=args.checkpoint_interval // BATCH_SIZE,  # Save every N env steps
        save_path="./models/",
        name_prefix="terraria_bot",
        save_replay_buffer=False,
        save_vecnormalize=False,
        verbose=1
    )
    
    print(f"\nStarting Training with {BATCH_SIZE} bots...")
    print(f"Checkpoints will be saved to: ./models/terraria_bot_<step>_steps.zip")
    print(f"Press Ctrl+C to stop and save\n")
    
    try:
        model.learn(
            total_timesteps=args.total_timesteps,
            callback=checkpoint_callback,
            reset_num_timesteps=False  # Continue counting from loaded model
        )
        
        # Save final model
        final_path = "models/terraria_bot_final"
        model.save(final_path)
        print(f"\n{'='*60}")
        print(f"Training completed! Final model saved to: {final_path}")
        print(f"{'='*60}")
        
    except KeyboardInterrupt:
        # Save on Ctrl+C
        interrupt_path = "models/terraria_bot_latest"
        model.save(interrupt_path)
        print(f"\n\n{'='*60}")
        print(f"Training interrupted! Model saved to: {interrupt_path}")
        print(f"Resume training with: python train_terraria.py --load-model {interrupt_path}")
        print(f"{'='*60}")
