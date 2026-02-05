"""
Zone Manager for RL Training
Handles loading zones from the database and sampling them for training.
"""
import json
import os
import random
from dataclasses import dataclass, field
from typing import List, Dict, Optional, Any
from collections import deque
import time

# Initialize random seed for proper zone sampling
random.seed()


@dataclass
class ZoneLocation:
    spawn_x: float
    spawn_y: float
    target_x: float
    target_y: float
    world_name: str = ""


@dataclass
class ZoneStats:
    times_trained: int = 0
    successful_attempts: int = 0
    failed_attempts: int = 0
    avg_completion_time: float = 0.0
    record_time: float = float('inf')
    last_trained: str = ""
    # Rolling history of recent attempts (max 100)
    recent_history: deque = field(default_factory=lambda: deque(maxlen=100))
    
    @property
    def success_rate(self) -> float:
        if self.times_trained == 0:
            return 0.0
        return self.successful_attempts / self.times_trained


@dataclass
class TrainingZone:
    zone_id: str
    original_location: ZoneLocation
    difficulty: int
    distance_tiles: float
    height_delta: float
    description: str = ""
    stats: ZoneStats = field(default_factory=ZoneStats)
    
    @classmethod
    def from_dict(cls, data: dict) -> 'TrainingZone':
        """Parse zone from JSON dictionary"""
        loc_data = data.get('OriginalLocation', {})
        location = ZoneLocation(
            spawn_x=loc_data.get('SpawnX', 0),
            spawn_y=loc_data.get('SpawnY', 0),
            target_x=loc_data.get('TargetX', 0),
            target_y=loc_data.get('TargetY', 0),
            world_name=loc_data.get('WorldName', '')
        )
        
        stats_data = data.get('Stats', {})
        stats = ZoneStats(
            times_trained=stats_data.get('TimesTrained', 0),
            successful_attempts=stats_data.get('SuccessfulAttempts', 0),
            failed_attempts=stats_data.get('FailedAttempts', 0),
            avg_completion_time=stats_data.get('AvgCompletionTime', 0),
            record_time=stats_data.get('RecordTime', float('inf'))
        )
        
        return cls(
            zone_id=data.get('ZoneId', 'unknown'),
            original_location=location,
            difficulty=data.get('Difficulty', 5),
            distance_tiles=data.get('DistanceTiles', 0),
            height_delta=data.get('HeightDelta', 0),
            description=data.get('Description', ''),
            stats=stats
        )


class ZoneManager:
    """Manages training zones - loading, sampling, and tracking stats"""
    
    def __init__(self, database_path: Optional[str] = None):
        self.zones: List[TrainingZone] = []
        self.database_path = database_path
        
        # Per-bot zone assignments
        self.bot_zones: Dict[int, TrainingZone] = {}
        self.bot_episode_start_times: Dict[int, float] = {}
        
        # Sampling strategy
        self.min_difficulty = 1
        self.max_difficulty = 10
        self.use_performance_sampling = True
        
        if database_path:
            self.load_database(database_path)
    
    def load_database(self, path: str) -> bool:
        """Load zones from JSON database"""
        try:
            if not os.path.exists(path):
                print(f"[ZoneManager] Database not found: {path}")
                return False
            
            with open(path, 'r') as f:
                data = json.load(f)
            
            zones_data = data.get('Zones', [])
            self.zones = [TrainingZone.from_dict(z) for z in zones_data]
            self.database_path = path
            
            print(f"[ZoneManager] Loaded {len(self.zones)} zones")
            for z in self.zones:
                print(f"  - {z.zone_id}: difficulty={z.difficulty}, distance={z.distance_tiles:.1f}")
            
            return True
            
        except Exception as e:
            print(f"[ZoneManager] Error loading database: {e}")
            return False
    
    def save_database(self):
        """Save updated zone stats back to database"""
        if not self.database_path or not self.zones:
            return
        
        try:
            # Read original to preserve terrain data
            with open(self.database_path, 'r') as f:
                data = json.load(f)
            
            # Update stats for each zone
            for zone in self.zones:
                for zone_data in data.get('Zones', []):
                    if zone_data.get('ZoneId') == zone.zone_id:
                        zone_data['Stats'] = {
                            'TimesTrained': zone.stats.times_trained,
                            'SuccessfulAttempts': zone.stats.successful_attempts,
                            'FailedAttempts': zone.stats.failed_attempts,
                            'AvgCompletionTime': zone.stats.avg_completion_time,
                            'RecordTime': zone.stats.record_time if zone.stats.record_time != float('inf') else None,
                            'LastTrained': zone.stats.last_trained
                        }
                        break
            
            with open(self.database_path, 'w') as f:
                json.dump(data, f, indent=2)
                
        except Exception as e:
            print(f"[ZoneManager] Error saving database: {e}")
    
    def get_zones_by_difficulty(self, min_diff: int = 1, max_diff: int = 10) -> List[TrainingZone]:
        """Filter zones by difficulty range"""
        return [z for z in self.zones if min_diff <= z.difficulty <= max_diff]
    
    def sample_zone(self) -> Optional[TrainingZone]:
        """Sample a single zone based on current strategy"""
        eligible = self.get_zones_by_difficulty(self.min_difficulty, self.max_difficulty)
        if not eligible:
            return None
        
        if not self.use_performance_sampling:
            result = random.choice(eligible)
            print(f"[ZoneManager] Sampled zone: {result.zone_id} (random)")
            return result
        
        # Performance-based sampling: weight by inverse success rate
        weights = []
        for zone in eligible:
            if zone.stats.success_rate < 0.3:
                weight = 3.0  # Really struggling - train more
            elif zone.stats.success_rate < 0.7:
                weight = 2.0  # Still learning
            else:
                weight = 0.5  # Mastered but keep fresh
            weights.append(weight)
        
        result = random.choices(eligible, weights=weights, k=1)[0]
        print(f"[ZoneManager] Sampled zone: {result.zone_id} (weight={weights[eligible.index(result)]}, success_rate={result.stats.success_rate:.2f})")
        return result
    
    def sample_zones_for_batch(self, num_bots: int) -> List[TrainingZone]:
        """Sample zones for all bots in a batch"""
        zones = []
        for _ in range(num_bots):
            zone = self.sample_zone()
            if zone:
                zones.append(zone)
            elif self.zones:
                zones.append(random.choice(self.zones))
        return zones
    
    def assign_zone_to_bot(self, bot_id: int, zone: TrainingZone):
        """Assign a zone to a specific bot and start episode timer"""
        self.bot_zones[bot_id] = zone
        self.bot_episode_start_times[bot_id] = time.time()
    
    def get_bot_zone(self, bot_id: int) -> Optional[TrainingZone]:
        """Get the zone assigned to a bot"""
        return self.bot_zones.get(bot_id)
    
    def record_episode_result(self, bot_id: int, success: bool, died: bool = False):
        """Record the result of an episode for a bot"""
        zone = self.bot_zones.get(bot_id)
        if not zone:
            return
        
        # Calculate completion time
        start_time = self.bot_episode_start_times.get(bot_id, time.time())
        completion_time = time.time() - start_time
        
        # Update zone stats
        zone.stats.times_trained += 1
        zone.stats.last_trained = time.strftime("%Y-%m-%dT%H:%M:%S")
        
        if success:
            zone.stats.successful_attempts += 1
            # Update average completion time
            n = zone.stats.successful_attempts
            old_avg = zone.stats.avg_completion_time
            zone.stats.avg_completion_time = old_avg + (completion_time - old_avg) / n
            # Update record time
            if completion_time < zone.stats.record_time:
                zone.stats.record_time = completion_time
        else:
            zone.stats.failed_attempts += 1
        
        # Add to history
        zone.stats.recent_history.append({
            'timestamp': time.time(),
            'success': success,
            'time': completion_time,
            'died': died
        })
    
    def get_zone_assignments_data(self, bot_ids: List[int]) -> bytes:
        """
        Create binary data for zone assignments to send to C#.
        Format per bot: spawn_x(4), spawn_y(4), target_x(4), target_y(4) = 16 bytes
        """
        import struct
        data = bytearray()
        
        for bot_id in bot_ids:
            zone = self.bot_zones.get(bot_id)
            if zone:
                loc = zone.original_location
                data.extend(struct.pack('ffff', loc.spawn_x, loc.spawn_y, loc.target_x, loc.target_y))
            else:
                # No zone - use zeros (will be ignored)
                data.extend(struct.pack('ffff', 0, 0, 0, 0))
        
        return bytes(data)
    
    def set_difficulty_range(self, min_diff: int, max_diff: int):
        """Set the difficulty range for zone sampling"""
        self.min_difficulty = max(1, min(10, min_diff))
        self.max_difficulty = max(1, min(10, max_diff))
        print(f"[ZoneManager] Difficulty range: {self.min_difficulty}-{self.max_difficulty}")
    
    def print_zone_stats(self):
        """Print statistics for all zones"""
        print("\n=== Zone Statistics ===")
        for zone in sorted(self.zones, key=lambda z: z.difficulty):
            sr = zone.stats.success_rate * 100
            avg_t = zone.stats.avg_completion_time
            rec_t = zone.stats.record_time if zone.stats.record_time != float('inf') else 0
            print(f"  [{zone.difficulty}] {zone.zone_id}: "
                  f"SR={sr:.1f}% ({zone.stats.successful_attempts}/{zone.stats.times_trained}) "
                  f"Avg={avg_t:.1f}s Record={rec_t:.1f}s")
        print()


# Default database path (relative to Training folder)
def get_default_database_path() -> str:
    """Get the default path to zones_database.json"""
    # Windows-compatible paths
    user_docs = os.path.expanduser("~")
    
    possible_paths = [
        # Terraria save location (Windows)
        os.path.join(user_docs, "Documents", "My Games", "Terraria", "tModLoader", "ModSources", "FirstMod_Sp", "TrainingZones", "zones_database.json"),
        # Relative to script (go up one level from Training/)
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "TrainingZones", "zones_database.json"),
        # Current dir
        "zones_database.json"
    ]
    
    print("[ZoneManager] Checking paths:")
    for i, path in enumerate(possible_paths):
        norm_path = os.path.normpath(path)
        exists = os.path.exists(norm_path)
        print(f"  {i+1}. {norm_path} - {'EXISTS' if exists else 'not found'}")
        if exists:
            return norm_path
    
    return os.path.normpath(possible_paths[0])  # Return first as default


if __name__ == "__main__":
    # Test loading
    manager = ZoneManager()
    path = get_default_database_path()
    print(f"Looking for database at: {path}")
    if manager.load_database(path):
        manager.print_zone_stats()
