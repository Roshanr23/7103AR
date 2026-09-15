# Shell fur for the squirrel

Blender's particle fur cannot travel through FBX, so the fur is recreated inside Unity with
the classic *shell* technique: the mesh is drawn 24 times, each copy pushed a little further
out along its normals, and the shader keeps only the pixels that belong to a strand tall enough
to reach that layer. Stacked together the layers read as dense fur.

## Files

| File | Purpose |
|------|---------|
| `ShellFur.cs` | Component that builds the hidden shell layers on any MeshRenderer. |
| `ShellFurBuiltIn.shader` | Layer shader for the Built-in Render Pipeline. |
| `ShellFurURP.shader` | Layer shader for the Universal Render Pipeline. |

Keep only the shader that matches your project's pipeline; the other one logs a harmless
compile error because its include files are not present. The component picks the right shader
automatically.

## Setup

1. Drag `SquirrelScene.fbx` into a scene.
2. Select the `Squirrel` child object and add the **Shell Fur** component. The defaults are tuned for the body.
3. Select `Squirrel_Tail` and add **Shell Fur** with roughly:
   Fur Length 0.16, Shell Count 28, Density 150, Thickness 0.9, Gravity 0.8.
4. Adjust in the Inspector; the layers rebuild after every change. From code, set fields then call `Rebuild()`.

Fur length is in the object's local units, so it scales with the model automatically if you
change the FBX Scale Factor.

## Parameters

- **Shell Count** – more layers give smoother fur at higher GPU cost. 16 to 32 is the useful range.
- **Fur Length** – how far the outermost layer sits from the skin.
- **Gravity** – tips sag toward world down; raise it for a hanging tail.
- **Density** – strands per local unit. Lower it if the fur looks like noise at your camera distance.
- **Thickness** – strand radius as a fraction of its cell; higher is fuller.
- **Root Shade** – darkens the base of the fur for depth.
- **Bare Below** – no fur where the albedo is darker than this, which keeps the eyes and nose clean.
- **Albedo Override** – the layers reuse the renderer's own texture unless you supply one here.

## Cost

Each layer redraws the whole mesh: the body is about 18k triangles, so 24 layers cost roughly
440k triangles. For mobile, drop Shell Count to 8 to 12 and Density to about 150.
