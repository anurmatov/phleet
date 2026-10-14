# Removing the Pi provider

Before upgrading, use the previous release to move **every** agent with `provider = 'pi'` to `claude` or `codex`, including disabled agents, and reprovision each one.

The new orchestrator refuses to start while any Pi row remains and names the agents in its startup error, so restore the previous orchestrator image to fix them rather than editing the database to bypass the guard.

This release removes the Pi CLI, extension, configuration and image-input column with a new migration whose Down restores that column with default false, while historical migrations stay unchanged and Claude/Codex attachment handling, ffmpeg, ffprobe, DejaVu fonts and fontconfig remain available.
