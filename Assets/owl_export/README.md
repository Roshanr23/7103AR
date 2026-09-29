# Snowy Owl - Unity import

Drop this whole folder into your project's `Assets/`.

## FIRST: if you already imported the squirrel, DELETE the `Fur/` folder

`ShellFur.cs` is a C# class, and a class can only be defined once per Unity
project. If the squirrel's `Fur/` is already in your Assets, this copy collides
with it:

    error CS0101: The namespace '<global namespace>' already contains a
    definition for 'ShellFur'

Delete whichever copy is the duplicate - they are byte-identical. `Fur/` ships
here only so this folder works in a project that has never seen the squirrel.

## FBX import settings (select SnowyOwl.fbx, then in the Inspector)

**Rig tab**  - Animation Type: **Generic** (NOT Humanoid - the rig is transform
nodes, not bones). Avatar Definition: Create From This Model. Apply.

**Animation tab** - make sure Import Animation is ticked, select the clip
(usually `Take 001`) and tick **Loop Time**. The animation is authored to loop
seamlessly; this is what makes it actually do so. Apply.

**Materials tab** - Material Creation Mode: Standard. Location: **Use External
Materials (Legacy)** so it picks up the `*_Albedo.png` files sitting beside it.
Apply.

## Hierarchy you get

    SnowyOwl
    |- Ctrl_Body            breathing
    |  |- Owl_Body
    |  |- Ctrl_Head         swivel + tilt
    |  |  |- Owl_Head
    |  |  |- Owl_Eye_L      blinks
    |  |  \- Owl_Eye_R      blinks
    |  |- Ctrl_Wing_Near    flap
    |  \- Ctrl_Wing_Far     flap
    |- Owl_Feet
    \- Environment          perch + background

## Animation

216 frames @ 24fps = 9s, looping.

  * head swivel with anticipation and overshoot; locks eyes with the camera
  * blinks at frames 46, 112, 180
  * both wings: 3-beat burst, 3s rest, repeating (peak 40 deg)
  * slow body breathing throughout

## Feathers

Blender particle hair cannot travel through FBX, so the feathers are recreated
with the shell technique. Select `Owl_Body`, `Owl_Head`, `Owl_Wing_Near` and
`Owl_Wing_Far`, then Add Component > **Shell Fur**. The defaults in `Fur/` were
tuned for a squirrel, so start shorter for owl feathers: Fur Length ~0.06,
Shell Count 24.

Delete whichever shader does not match your pipeline - keep `ShellFurURP.shader`
for URP or `ShellFurBuiltIn.shader` for Built-in. The unused one logs a harmless
compile error because its include files are absent.

## Troubleshooting

  * owl tiny or giant -> Scale Factor, Model tab
  * animation does not play -> Rig tab was not set to Generic
  * untextured -> Materials tab, re-Apply with External Materials
