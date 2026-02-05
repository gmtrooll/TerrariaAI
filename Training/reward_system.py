
import numpy as np
import collections

class RewardSystem:
    """
    Calculates rewards using Geodesic (Path) Distance to guide bots around walls.
    """
    def __init__(self):
        self.prev_distances = {} # bot_id -> float (path distance)
        
    def reset(self, bot_id):
        """Reset tracker for a specific bot"""
        self.prev_distances[bot_id] = None 

    def calculate_geodesic_distance(self, grid, target_dx, target_dy):
        """
        Run BFS to find shortest path from Center (20,20) to Target (20+dx, 20+dy).
        Grid: 1.0 = Wall, 0.0 = Air.
        """
        start_x, start_y = 20, 20
        target_x = int(20 + target_dx)
        target_y = int(20 + target_dy)
        
        # Clamp target to grid bounds
        target_x = max(0, min(39, target_x))
        target_y = max(0, min(39, target_y))
        
        # If target IS start (reached), dist is 0
        if start_x == target_x and start_y == target_y:
            return 0.0
            
        # BFS Initialization
        # Queue: (x, y, dist)
        queue = collections.deque([(start_x, start_y, 0)])
        visited = set([(start_x, start_y)])
        
        # Dimensions
        rows, cols = 40, 40
        
        while queue:
            cx, cy, dist = queue.popleft()
            
            # Reached target?
            if cx == target_x and cy == target_y:
                return float(dist)
            
            # Neighbors (Up, Down, Left, Right)
            for nx, ny in [(cx+1, cy), (cx-1, cy), (cx, cy+1), (cx, cy-1)]:
                if 0 <= nx < cols and 0 <= ny < rows:
                    if (nx, ny) not in visited:
                        # Check collision (Assuming < 0.9 is passable)
                        # grid is (1, 40, 40) usually, need to handle input shape
                        cell_val = grid[0, ny, nx] if len(grid.shape) == 3 else grid[ny, nx]
                        
                        if cell_val < 0.9: 
                            visited.add((nx, ny))
                            queue.append((nx, ny, dist + 1))
                            
        # Target unreachable? Return penalties based on Euclidean fallback
        # If we can't find a path, return a high value (e.g. 100)
        return 50.0 # Cap distance penalty

    def calculate_reward(self, bot_id, raw_dist, flags, grid, stats):
        """
        Calculate step reward with Scent/Pathfinding logic.
        
        Args:
            bot_id: Entity ID
            raw_dist: Raw Euclidean distance (from C#) - Used as fallback/check
            flags: Status flags
            grid: 40x40 Vision Grid
            stats: Observation vector (Indices 44,45 are clamped target delta)
        """
        is_done = (flags & 1) != 0
        is_win = (flags & 2) != 0
        is_die = (flags & 4) != 0
        is_timeout = (flags & 8) != 0
        
        reward = 0.0
        info = {}
        
        # 1. Terminal Rewards
        if is_win:
            reward += 20.0
            self.reset(bot_id)
            return reward, True, {"outcome": "win"}
        if is_die:
            reward -= 5.0
            self.reset(bot_id)
            return reward, True, {"outcome": "died"}
        if is_timeout:
            reward -= 1.0
            self.reset(bot_id)
            return reward, True, {"outcome": "timeout"}
            
        # 2. Path Finding Reward "Scent"
        if raw_dist != -1: # raw_dist check ensures target exists
            # Extract Target Delta from Stats (Last 2 elements)
            # Stats layout: ... [44] = Clamp(dX/16), [45] = Clamp(dY/16)
            # These are already in "Blocks", clamped to +/- 20
            t_dx = stats[-2]
            t_dy = stats[-1]
            
            # Calculate Path Distance
            path_dist = self.calculate_geodesic_distance(grid, t_dx, t_dy)
            
            if self.prev_distances.get(bot_id) is None:
                self.prev_distances[bot_id] = path_dist
            else:
                prev = self.prev_distances[bot_id]
                progress = prev - path_dist
                
                # Reward Scaling: 0.5 points per block closer
                reward += progress * 0.5
                
                self.prev_distances[bot_id] = path_dist
                
        return reward, False, {}
