# Wiki note: models and animations (retargeting, the creature template)

What changed for the public guide, with suggested text.

## New: retargeting animations

Where: the Animations page, after "Clips"; one line on the Creature blocks page under Animation.

Suggested text:

> **Animations from another rig.** Animation packs and other tools rarely use your skeleton's joint names or proportions. A clips step (or an animation
> block) with `retarget` plays them anyway: name the skeleton they should play on and pair the joints.
>
> ```json
> "animations": { "golem-moves": { "clips": "models/anim-pack.glb", "skeleton": "golem",
>     "retarget": { "map": { "mixamorig:Hips": "pelvis", "mixamorig:Spine": "spine", "mixamorig:Head": "head" } },
>     "idle": "Idle", "walk": "Walking" } }
> ```
>
> Joints of the same name pair by themselves. Bone lengths and rest orientations may differ; the two rest poses must show the same posture (T-pose for
> T-pose), or `reference` names a frame of the pack that does. The hips' travel is scaled by the rigs' heights. Joints with no pair keep their rest pose.
> Works onto a skeleton of your own and onto one of the game's (`j<index>` joint names).

Status line for the page: proven offline (the sample's animations on rigs with other names, turned rest frames and other sizes; 55 of the game's clips
re-expressed on another rig and retargeted back, equal within the encoder's tolerance) and on a dedicated server (a creature playing retargeted clips,
its engine joints following the motion as authored). Not yet seen drawn in the game client.

Not done: foot IK after a retarget (feet can slide when leg lengths differ), mirroring left onto right.

## New template: `kiln-body`

Where: the MDK / templates table and the "Your own creature" walkthrough.

Suggested text:

> `kiln new body MyGolem` makes a creature of your own files: a skinned glTF (`models/creature.glb`, replace it with your Blender export) as its body
> and animations, and blocks that give it an AI and an attack. `kiln build` cooks the skeleton, clips, state machine and model on your machine. In game,
> `/my-golem` (admins) spawns one next to you. Players need the mod (`clients: mod`).
>
> From Blender: File > Export > glTF 2.0, +Y up, the armature at rest, each animation an action. Name the actions as the blocks name the clips.

## Corrections for existing pages

- The Animations page can drop any wording that says a game skeleton needs the game's own joint names: with `retarget`, it doesn't.
- Armor pieces of your own (`Armor.Look`, a worn look) and creature bodies of your own are both cooked skinned today; neither has been seen drawn in the
  client yet.
