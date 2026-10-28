New weapon models (work in progress, not loaded by the game)

isorim_weapons_batch1.zip   monosword, plasmasword, zeushammer, toxbomb launcher, incinerator
isorim_weapons_batch2.zip   Eltex staff, flamebow, tox grenade, nerve spiker, hellcat rifle
isorim_weapons_batch3.zip   jade knife, beam graser, beam repeater, persona monosword,
                            persona plasmasword, persona zeushammer
make_weapons.py             the Blender (bpy) script that builds them:
                            python3 make_weapons.py -- <output folder>
                            (needs Blender's bpy module; builds the batch named in MODELS)

Each zip has the .obj files (for Models/), renders next to the game's sprites, and notes.
Not yet bound in Defs/Models.xml. IsoRim currently draws weapons from their sprites, so these
models won't show in game until that code is changed.
