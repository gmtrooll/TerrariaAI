"""
Configuration loader for Terraria RL training
Loads settings from training_config.json
"""
import json
import os
from typing import Dict, Any


def load_training_config(config_path: str = None) -> Dict[str, Any]:
    """Load training configuration from JSON file"""
    if config_path is None:
        # Default to Training folder
        script_dir = os.path.dirname(os.path.abspath(__file__))
        config_path = os.path.join(script_dir, "training_config.json")
    
    if not os.path.exists(config_path):
        print(f"[Config] WARNING: Config file not found at {config_path}")
        print("[Config] Using default values")
        return get_default_config()
    
    try:
        with open(config_path, 'r') as f:
            config = json.load(f)
        
        # Flatten nested structure for easier access
        flat_config = {}
        for category, values in config.items():
            if isinstance(values, dict) and not category.startswith('_'):
                flat_config.update(values)
        
        print(f"[Config] Loaded configuration from {config_path}")
        print(f"[Config] Bot count: {flat_config.get('num_bots', 16)}")
        
        return flat_config
        
    except Exception as e:
        print(f"[Config] ERROR loading config: {e}")
        print("[Config] Using default values")
        return get_default_config()


def get_default_config() -> Dict[str, Any]:
    """Return default configuration values"""
    return {
        # Core settings
        'num_bots': 16,
        'port': 5555,
        'max_steps_per_zone': 1500,
        'frame_skip': 4,
        
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
        
        # Zone settings
        'zone_min_difficulty': 1,
        'zone_max_difficulty': 10,
        'use_performance_sampling': True
    }


if __name__ == "__main__":
    # Test config loading
    config = load_training_config()
    print("\nLoaded config:")
    for key, value in config.items():
        print(f"  {key}: {value}")
