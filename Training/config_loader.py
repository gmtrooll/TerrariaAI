"""
Configuration loader for Terraria RL training
Loads settings from training_config.json (sections are flattened into one dict).
"""
import json
import os
from typing import Dict, Any


def get_default_config() -> Dict[str, Any]:
    """Return default configuration values (also used for keys missing from the file)"""
    return {
        # Core settings
        'num_bots': 16,
        'port': 5555,
        'max_steps_per_zone': 1500,
        'seed': None,

        # Observation
        'frame_skip': 4,      # game ticks per decision (read by the mod)
        'frame_stack': 1,     # >1 wraps the env in VecFrameStack
        
        # Training hyperparameters
        'learning_rate': 0.0002,
        'n_steps': 512,
        'batch_size': 512,
        'n_epochs': 4,
        'gamma': 0.99,
        'gae_lambda': 0.95,
        'clip_range': 0.2,
        'ent_coef': 0.01,
        'vf_coef': 0.5,
        'max_grad_norm': 0.5,

        # Reward
        'win_reward': 20.0,
        'death_penalty': -5.0,
        'timeout_penalty': -1.0,
        'progress_weight': 0.5,
        'max_progress_per_step': 5.0,

        # Path distance field (read by the mod)
        'max_jump_tiles': 14,
        'field_radius_tiles': 96,
        'off_path_penalty_tiles': 10.0,
        
        # Zone settings
        'zone_min_difficulty': 1,
        'zone_max_difficulty': 10,
        'use_performance_sampling': True,
        'zone_database_path': None,

        # Logging
        'debug': False,
    }


def default_config_path() -> str:
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "training_config.json")


def load_training_config(config_path: str = None, verbose: bool = True) -> Dict[str, Any]:
    """Load training configuration from JSON file"""
    if config_path is None:
        config_path = default_config_path()
    
    config = get_default_config()
    if not os.path.exists(config_path):
        print(f"[Config] WARNING: Config file not found at {config_path}, using default values")
        return config
    
    try:
        with open(config_path, 'r') as f:
            raw = json.load(f)
    except (OSError, ValueError) as e:
        print(f"[Config] ERROR loading config {config_path}: {e}; using default values")
        return config

    # Flatten nested structure for easier access
    for category, values in raw.items():
        if isinstance(values, dict) and not category.startswith('_'):
            config.update(values)
    
    if verbose:
        print(f"[Config] Loaded configuration from {config_path} (num_bots={config['num_bots']})")
    return config


if __name__ == "__main__":
    # Test config loading
    config = load_training_config()
    print("\nLoaded config:")
    for key, value in config.items():
        print(f"  {key}: {value}")
