"""
Terraria RL Training Dashboard
A Streamlit-based control panel for training your Terraria RL bots.
Run with: streamlit run dashboard.py
"""

import streamlit as st
import plotly.graph_objects as go
import plotly.express as px
import time
import os
import subprocess
import sys
from train_terraria import TrainingManager, TrainingConfig, LOGS_DIR

# --- PAGE CONFIG ---
st.set_page_config(
    page_title="Terraria RL Dashboard",
    page_icon="🎮",
    layout="wide",
    initial_sidebar_state="expanded"
)

# --- CUSTOM CSS ---
st.markdown("""
<style>
    .stButton > button {
        width: 100%;
        border-radius: 8px;
        font-weight: bold;
    }
    .metric-card {
        background: linear-gradient(135deg, #1a1a2e 0%, #16213e 100%);
        padding: 20px;
        border-radius: 12px;
        border: 1px solid #333;
    }
    .status-training {
        color: #00ff88;
        font-weight: bold;
    }
    .status-idle {
        color: #888;
    }
    .status-error {
        color: #ff4444;
    }
</style>
""", unsafe_allow_html=True)

# --- SESSION STATE INIT ---
if "manager" not in st.session_state:
    st.session_state.manager = None
if "config" not in st.session_state:
    st.session_state.config = TrainingConfig()
if "tensorboard_process" not in st.session_state:
    st.session_state.tensorboard_process = None


def get_manager() -> TrainingManager:
    """Get or create the training manager"""
    if st.session_state.manager is None:
        st.session_state.manager = TrainingManager(st.session_state.config)
    return st.session_state.manager


def format_time(seconds: float) -> str:
    """Format seconds to HH:MM:SS"""
    hours = int(seconds // 3600)
    minutes = int((seconds % 3600) // 60)
    secs = int(seconds % 60)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}"


# --- HEADER ---
st.title("🎮 Terraria RL Training Dashboard")

# Status indicator
manager = get_manager()
metrics = manager.get_metrics()
status = metrics.get("status", "idle")

if status == "training":
    st.success(f"🟢 **Status:** Training in progress...")
elif "error" in status.lower():
    st.error(f"🔴 **Status:** {status}")
elif status == "idle":
    st.info(f"⚪ **Status:** Ready to train")
else:
    st.warning(f"🟡 **Status:** {status}")

# --- SIDEBAR: CONFIG ---
with st.sidebar:
    st.header("⚙️ Configuration")
    
    # Model Management
    st.subheader("📁 Model")
    
    models = manager.get_available_models()
    model_options = ["[Create New]"] + models
    
    selected_model = st.selectbox(
        "Select Model",
        options=model_options,
        index=0 if not metrics.get("model_path") else (
            model_options.index(metrics["model_path"]) if metrics.get("model_path") in model_options else 0
        )
    )
    
    col1, col2 = st.columns(2)
    with col1:
        if st.button("📂 Load", disabled=manager.is_training):
            if selected_model == "[Create New]":
                manager.create_new_model()
                st.rerun()
            else:
                manager.load_model(selected_model)
                st.rerun()
    
    with col2:
        save_name = st.text_input("Save as:", value="terraria_bot_latest", label_visibility="collapsed")
        if st.button("💾 Save", disabled=not metrics.get("model_loaded")):
            manager.save_model(save_name)
            st.rerun()
    
    st.divider()
    
    # Hyperparameters
    st.subheader("🔧 Hyperparameters")
    
    num_bots = st.slider(
        "Number of Bots",
        min_value=1,
        max_value=max(64, st.session_state.config.num_bots),
        value=st.session_state.config.num_bots,
        step=1,
        disabled=manager.is_training,
        help="Number of bots / parallel environments. Must equal num_bots in the training_config.json "
             "the mod loaded, or the connection is rejected."
    )
    
    learning_rate = st.select_slider(
        "Learning Rate",
        options=[0.00001, 0.00005, 0.0001, 0.0002, 0.0003, 0.0005, 0.001],
        value=st.session_state.config.learning_rate,
        disabled=manager.is_training,
        format_func=lambda x: f"{x:.5f}"
    )
    
    entropy_coef = st.slider(
        "Entropy Coefficient",
        min_value=0.0,
        max_value=0.1,
        value=st.session_state.config.entropy_coef,
        step=0.005,
        disabled=manager.is_training,
        help="Higher = more exploration"
    )
    
    batch_size = st.select_slider(
        "PPO Minibatch Size",
        options=sorted({512, 1024, 2048, 4096, 8192, st.session_state.config.batch_size}),
        value=st.session_state.config.batch_size,
        disabled=manager.is_training
    )
    
    total_timesteps = st.number_input(
        "Total Timesteps",
        min_value=100_000,
        max_value=100_000_000,
        value=st.session_state.config.total_timesteps,
        step=500_000,
        disabled=manager.is_training
    )
    
    checkpoint_interval = st.number_input(
        "Checkpoint Interval",
        min_value=10_000,
        max_value=500_000,
        value=st.session_state.config.checkpoint_interval,
        step=10_000,
        disabled=manager.is_training
    )
    
    device = st.radio(
        "Device",
        options=["cuda", "cpu"],
        index=0 if st.session_state.config.device == "cuda" else 1,
        horizontal=True,
        disabled=manager.is_training
    )
    
    # Apply config changes
    if not manager.is_training:
        st.session_state.config.num_bots = num_bots
        st.session_state.config.learning_rate = learning_rate
        st.session_state.config.entropy_coef = entropy_coef
        st.session_state.config.batch_size = batch_size
        st.session_state.config.total_timesteps = total_timesteps
        st.session_state.config.checkpoint_interval = checkpoint_interval
        st.session_state.config.device = device
        manager.update_config(
            num_bots=num_bots,
            learning_rate=learning_rate,
            entropy_coef=entropy_coef,
            batch_size=batch_size,
            total_timesteps=total_timesteps,
            checkpoint_interval=checkpoint_interval,
            device=device
        )

# --- MAIN CONTENT ---

# Training Controls
st.header("🎯 Training Controls")

col1, col2, col3 = st.columns([1, 1, 2])

with col1:
    if not manager.is_training:
        if st.button("▶️ Start Training", type="primary", use_container_width=True):
            manager.start_training()
            st.rerun()
    else:
        if st.button("⏹️ Stop Training", type="secondary", use_container_width=True):
            manager.stop_training()
            st.rerun()

with col2:
    if st.button("📊 Open TensorBoard", use_container_width=True):
        if st.session_state.tensorboard_process is None or st.session_state.tensorboard_process.poll() is not None:
            st.session_state.tensorboard_process = subprocess.Popen(
                [sys.executable, "-m", "tensorboard", "--logdir", LOGS_DIR, "--port", "6006"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL
            )
            time.sleep(2)
        st.markdown("[🔗 Open TensorBoard](http://localhost:6006)", unsafe_allow_html=True)

with col3:
    # Progress bar
    raw_progress = metrics.get("timesteps", 0) / max(metrics.get("total_timesteps", 1), 1)
    progress = min(max(raw_progress, 0.0), 1.0)
    st.progress(progress, text=f"Progress: {metrics.get('timesteps', 0):,} / {metrics.get('total_timesteps', 0):,} steps ({raw_progress*100:.1f}%)")

# --- METRICS ---
st.header("📈 Live Metrics")

col1, col2, col3, col4, col5 = st.columns(5)

with col1:
    st.metric(
        label="Mean Reward",
        value=f"{metrics.get('mean_reward', 0):.2f}",
        delta=None
    )

with col2:
    st.metric(
        label="Steps/Second",
        value=f"{metrics.get('fps', 0):.0f}",
        delta=None
    )

with col3:
    st.metric(
        label="Elapsed Time",
        value=format_time(metrics.get("elapsed_time", 0)),
        delta=None
    )

with col4:
    st.metric(
        label="Episode Length",
        value=f"{metrics.get('mean_episode_length', 0):.0f}",
        delta=None
    )

with col5:
    st.metric(
        label="Success Rate (100 ep)",
        value=f"{metrics.get('success_rate', 0) * 100:.0f}%",
        delta=None
    )

# --- REWARD CHART ---
reward_history = metrics.get("reward_history", [])

if reward_history:
    st.subheader("📉 Reward History")
    
    fig = go.Figure()
    fig.add_trace(go.Scatter(
        y=reward_history,
        mode='lines',
        name='Episode Reward',
        line=dict(color='#00ff88', width=1),
        fill='tozeroy',
        fillcolor='rgba(0, 255, 136, 0.1)'
    ))
    
    # Add rolling average
    if len(reward_history) > 10:
        import numpy as np
        window = min(50, len(reward_history))
        rolling_avg = np.convolve(reward_history, np.ones(window)/window, mode='valid')
        fig.add_trace(go.Scatter(
            y=rolling_avg,
            mode='lines',
            name=f'Rolling Avg ({window})',
            line=dict(color='#ff8800', width=2)
        ))
    
    fig.update_layout(
        template="plotly_dark",
        height=300,
        margin=dict(l=0, r=0, t=30, b=0),
        legend=dict(orientation="h", yanchor="bottom", y=1.02),
        xaxis_title="Episode",
        yaxis_title="Reward"
    )
    
    st.plotly_chart(fig, use_container_width=True)
else:
    st.info("📊 Reward chart will appear once training starts and episodes complete.")

# --- AUTO REFRESH ---
if manager.is_training:
    time.sleep(2)
    st.rerun()

# --- FOOTER ---
st.divider()
st.caption("🎮 Terraria RL Training Dashboard | Press Ctrl+C in terminal to fully stop")
