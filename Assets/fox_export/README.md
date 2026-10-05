# Red fox - Unity import

Rigged (FoxRig, 22 bones) with skinned meshes: Fox_Fur, Fox_Face, Fox_Whiskers.

The clip is ONE in-place gait loop of 48 frames at 24 fps (Blender frames
62-109). The fox covers 0.550 m/s at its authored size; whatever moves it
in Unity should travel at that speed times its scale, or the paws will skate.

Import: Rig = Generic, Animation = Loop Time, Materials = External (Legacy).
Shell fur on Fox_Fur only - Fox_Face must stay bare. The fox's legs are near-black, so
lower Shell Fur's Bare Below (~0.02) or the default skips fur on them.
