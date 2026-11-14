# Suffixes for scenarios
List of actual suffixes for scenario swap feature.

Also supported next things:
- `lnXXX` (where XXX is a number of line) part in scenario name - for multiple unique lines (or something else). Examples: `move_100ln01.json`, `move_100ln01_leader.json`
- `no_XXX` (where XXX is a number of line) part in scenario name - for NO answer. Examples: `chara_bmail100no01.json`, `chara_bmail100no01_leader.json`
- `n_XXX` (where XXX is a number of line) part in scenario name - for 難聴 ("What is that?") answer. Examples: `ask_100n01.json`, `ask_100n01_leader.json`

### Traits
Each trait also can use universal suffix `_traitXXX` (where XXX is a ID of trait), if option for that enabled in plugin settings. Also that suitable for modded traits, which not hardcoded into plugin.

Vanilla traits:
- `_easygoing`
- `_affable`
- `_bwguys`
- `_bwgirls`
- `_charming`
- `_chivalrous`
- `_trendy`
- `_obedient`
- `_positive`
- `_shy`
- `_jealous`
- `_melancholy`
- `_perverted`
- `_serious`
- `_calm`
- `_nervous`
- `_impulsive`
- `_absentminded`
- `_hotheaded`
- `_carnivorous`
- `_herbivorous`
- `_meddlesome`
- `_leader`
- `_musclehead`
- `_chatty`
- `_gluttonous`
- `_diligent`
- `_romantic`
- `_selfish`
- `_singleminded`
- `_indecisive`
- `_schemer`
- `_inoffensive`
- `_workaholic`
- `_wild`
- `_masochist`
- `_evil`
- `_thickheaded`
- `_blind`
- `_lucky`

### Specific cases
Some small or complex cases, which requires a unique answer.

Sex-related:
- `_male`
- `_female`

Fake answers:
- `_fakeyes`
- `_fakeno`
- `_schemerfakeyes` - additional case specially for schemers (used by "Stop harassing..." action)

Love-related:
- `_inlove` - when both characters in love
- `_singlemindinlove` - NPC has "Singleminded" trait and they in love with PC
- `_guesscheat` - NPC suspect what PC is cheating
