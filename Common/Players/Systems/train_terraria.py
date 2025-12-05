import socket
import struct
import numpy as np
import gymnasium as gym
from gymnasium import spaces
from stable_baselines3 import PPO

class TerrariaSocketEnv(gym.Env):
    def __init__(self):
        super(TerrariaSocketEnv, self).__init__()
        
        # CONFIGURATION
        self.GRID_SIZE = 40
        self.STATS_SIZE = 44  # Matches RLInterface.cs (Check this number!)
        self.GRID_INPUTS = self.GRID_SIZE * self.GRID_SIZE
        
        # 1. DEFINE OBSERVATION SPACE (Dictionary)
        # This tells the AI: "I have two eyes. One sees a grid, one sees stats."
        self.observation_space = spaces.Dict({
            "vision": spaces.Box(low=0, high=1, shape=(1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32),
            "stats":  spaces.Box(low=-np.inf, high=np.inf, shape=(self.STATS_SIZE,), dtype=np.float32)
        })
        
        # 2. DEFINE ACTION SPACE
        self.action_space = spaces.Discrete(4) 
        
        # Networking Setup
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.bind(('127.0.0.1', 5000))
        self.server.listen(1)
        self.conn = None
        self.wait_for_connection()

    def wait_for_connection(self):
        print("Waiting for Terraria...")
        self.conn, self.addr = self.server.accept()
        print(f"Connected to {self.addr}")

    def step(self, action):
        if self.conn is None: return self.reset()[0], 0, True, False, {}

        try:
            # 1. SEND ACTION
            self.conn.sendall(struct.pack('i', int(action)))

            # 2. RECEIVE STATS (44 Floats)
            stats_bytes = self.recv_all(self.STATS_SIZE * 4)
            stats_data = np.array(struct.unpack(f'{self.STATS_SIZE}f', stats_bytes), dtype=np.float32)

            # 3. RECEIVE GRID (1600 Floats)
            grid_bytes = self.recv_all(self.GRID_INPUTS * 4)
            grid_flat = np.array(struct.unpack(f'{self.GRID_INPUTS}f', grid_bytes), dtype=np.float32)
            grid_reshaped = grid_flat.reshape((1, self.GRID_SIZE, self.GRID_SIZE))

            # 4. RECEIVE REWARD & DONE
            extra_data = self.recv_all(5)
            reward = struct.unpack('f', extra_data[0:4])[0]
            terminated = struct.unpack('?', extra_data[4:5])[0]

            # 5. PACKET THE DICTIONARY
            obs = {
                "vision": grid_reshaped,
                "stats": stats_data
            }

            return obs, reward, terminated, False, {}

        except (BrokenPipeError, struct.error):
            print("Connection Lost.")
            self.conn = None
            return self.reset()[0], 0, True, False, {}

    def reset(self, seed=None):
        if self.conn is None: self.wait_for_connection()
        
        # Return blank dictionary
        obs = {
            "vision": np.zeros((1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32),
            "stats": np.zeros((self.STATS_SIZE,), dtype=np.float32)
        }
        return obs, {}

    def recv_all(self, n):
        data = b''
        while len(data) < n:
            packet = self.conn.recv(n - len(data))
            if not packet: raise BrokenPipeError()
            data += packet
        return data

if __name__ == "__main__":
    env = TerrariaSocketEnv()

    # CRITICAL CHANGE: Use "MultiInputPolicy" instead of "CnnPolicy"
    # This policy knows how to handle the Dictionary input (CNN for vision, MLP for stats)
    model = PPO(
        "MultiInputPolicy", 
        env, 
        verbose=1,
        device="cuda",
        # We still need this fix for the vision part
        policy_kwargs=dict(normalize_images=False)
    )

    print("Starting training...")
    try:
        model.learn(total_timesteps=1000000)
    except KeyboardInterrupt:
        model.save("terraria_bot_v2")