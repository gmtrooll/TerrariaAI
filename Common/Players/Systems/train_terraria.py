import socket
import struct
import numpy as np
import time
import gymnasium as gym
from gymnasium import spaces
from stable_baselines3 import PPO
from stable_baselines3.common.vec_env import VecEnv

class TerrariaVecEnv(VecEnv):
    def __init__(self, num_envs=16): # CHANGED: Default to 16
        # CONFIGURATION
        self.GRID_SIZE = 40
        self.STATS_SIZE = 46 
        self.GRID_INPUTS = self.GRID_SIZE * self.GRID_SIZE
        self.NUM_ENVS = num_envs
        
        # PRE-CALCULATE PACKET SIZES
        self.STATS_BYTES = self.NUM_ENVS * self.STATS_SIZE * 4
        self.GRID_BYTES = self.NUM_ENVS * self.GRID_INPUTS * 4
        self.EXTRA_BYTES = self.NUM_ENVS * 5 
        self.TOTAL_PACKET_SIZE = self.STATS_BYTES + self.GRID_BYTES + self.EXTRA_BYTES
        
        # Buffer will automatically size up to ~105KB for 16 bots
        self.recv_buffer = bytearray(self.TOTAL_PACKET_SIZE)

        # Performance Tracking
        self.frame_count = 0
        self.last_time = time.time()

        # Define Spaces
        observation_space = spaces.Dict({
            "vision": spaces.Box(low=0, high=1, shape=(1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32),
            "stats":  spaces.Box(low=-np.inf, high=np.inf, shape=(self.STATS_SIZE,), dtype=np.float32)
        })
        action_space = spaces.MultiBinary(3)

        super().__init__(num_envs, observation_space, action_space)
        
        # Networking
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1) 
        self.server.bind(('127.0.0.1', 5000))
        self.server.listen(1)
        self.conn = None
        self.wait_for_connection()

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

    def step_async(self, actions):
        if self.conn is None: return
        try:
            # FIX: MultiBinary Packing
            # 'actions' is a numpy array of shape (16, 3) -> [[0,1,0], [1,1,1]...]
            # We pack it into a single integer bitmask for C#
            for act in actions:
                flags = 0
                # Check indices: 0=Left, 1=Right, 2=Jump
                if act[0] > 0.5: flags |= 1 
                if act[1] > 0.5: flags |= 2 
                if act[2] > 0.5: flags |= 4 
                
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
                r = struct.unpack_from('f', self.recv_buffer, agent_offset)[0]
                d = struct.unpack_from('?', self.recv_buffer, agent_offset + 4)[0]
                
                rewards.append(r)
                dones.append(d)
                
                if d:
                    infos[i]["terminal_observation"] = {
                        "vision": grid_batch[i],
                        "stats": stats_batch[i]
                    }

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
        if self.conn:
            self.conn.close()

    def _get_dummy_batch(self):
        obs = {
            "vision": np.zeros((self.NUM_ENVS, 1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32),
            "stats": np.zeros((self.NUM_ENVS, self.STATS_SIZE), dtype=np.float32)
        }
        return obs, np.zeros(self.NUM_ENVS), np.zeros(self.NUM_ENVS, dtype=bool), [{}] * self.NUM_ENVS

# --- RUN TRAINING ---

if __name__ == "__main__":
    # Ensure this matches C# BATCH_SIZE
    BATCH_SIZE = 16 
    
    env = TerrariaVecEnv(num_envs=BATCH_SIZE)

    model = PPO(
        "MultiInputPolicy", 
        env, 
        verbose=1,
        
        device="cuda",      
        batch_size=2048, # Increased batch size for 16 bots
        
        learning_rate=0.0002,
        ent_coef=0.01,
        n_steps=512,           
        n_epochs=4,            
        
        policy_kwargs=dict(normalize_images=False)
    )

    print(f"Starting Training with {BATCH_SIZE} bots...")
    try:
        model.learn(total_timesteps=5000000)
    except KeyboardInterrupt:
        model.save("terraria_multibot_v1")