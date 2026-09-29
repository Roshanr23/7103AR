# White-tailed Deer - Unity import

Drop this whole folder into your project's `Assets/`.

## FIRST: if another model already brought in `Fur/`, DELETE this copy

`ShellFur.cs` is a C# class and can only be defined once per Unity project.
A second copy gives:

    error CS0101: The namespace '<global namespace>' already contains a
    definition for 'ShellFur'

The copies are byte-identical, so delete whichever is the duplicate. `Fur/`
ships here only so this folder works in a project that has never seen the
squirrel or the owl.

## FBX import settings (select WhiteTailedDeer.fbx, then in the Inspector)

**Rig tab** - Animation Type: **Generic** (NOT Humanoid - the rig is transform
nodes, not bones). Avatar Definition: Create From This Model. Apply.

**Animation tab** - Import Animation ticked, select the clip (usually
`Take 001`) and tick **Loop Time**. The 10s cycle is authored to loop
seamlessly. Apply.

**Materials tab** - Material Creation Mode: Standard. Location: **Use External
Materials (Legacy)** so it picks up the `*_Albedo.png` files beside it. Apply.

## Hierarchy you get

    WhiteTailedDeer
    |- Ctrl_Body              breathing
    |  |- Deer_Body
    |  \- Ctrl_Neck           trails the head by 4 frames
    |     |- Deer_Neck
    |     \- Ctrl_Head        turn + nod
    |        |- Deer_Head
    |        |- Deer_Eye_0    blinks
    |        |- Deer_Eye_1    blinks
    |        |- Ctrl_Ear0     independent flicks
    |        |  \- Deer_Ear0
    |        \- Ctrl_Ear1
    |           \- Deer_Ear1
    |- Deer_Legs              planted, never inherits breathing
    |- Deer_SnowMounds
    \- Environment            ground, backdrop, trees

## Animation

240 frames @ 24fps = 10s, looping.

  * 7 independent, irregular ear flicks with follow-through
  * alert head turn, with the neck trailing 4 frames behind for overlap
  * blinks at frames 96 and 190
  * slow body breathing, 4 cycles that divide the loop exactly

## Fur

Blender particle fur cannot travel through FBX, so it is recreated with the
shell technique. Add **Shell Fur** to `Deer_Body`, `Deer_Head`, `Deer_Neck`,
`Deer_Ear0` and `Deer_Ear1`.

`furLength` is in the object's LOCAL units, and these parts differ a lot in
size, so one value will not fit all. Rough starting points:

| Object      | approx height | Fur Length |
|-------------|---------------|------------|
| `Deer_Body` | 1.16          | 0.06       |
| `Deer_Neck` | 0.96          | 0.05       |
| `Deer_Head` | 0.79          | 0.045      |
| `Deer_Ear*` | 0.48          | 0.03       |

Delete whichever shader does not match your pipeline - `ShellFurURP.shader`
for URP, `ShellFurBuiltIn.shader` for Built-in.

## Troubleshooting

  * deer tiny or giant -> Scale Factor, Model tab
  * animation does not play -> Rig tab was not set to Generic
  * untextured -> Materials tab, re-Apply with External Materials
  * no fur visible -> furLength is too short for that object's size, see above
