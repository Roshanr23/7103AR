# Arctic hare - Unity import

Rigged (HareRig, 9 bones) with skinned meshes Hare_Fur, Hare_Face, Hare_Whiskers.
The clip is the whole sit / three-hop / settle sequence, frames 1-144 at 24 fps.

The hops' forward travel was removed from the clip (the arc and the tilt stay). hare_travel.json
holds the distance covered on each frame (1.260 m per loop); move the hare by exactly that,
times its scale, or it will slide while sitting or hop without going anywhere.

HareGround.fbx is a separate, static patch of the bark chips from the Blender scene.
