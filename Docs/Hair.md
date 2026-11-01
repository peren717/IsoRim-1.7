# IsoRim hair models: vanilla hairstyles and the 3D model each one uses

IsoRim draws all 71 vanilla (Core + DLC) hairstyles with **22 shared 3D shapes**, so some 3D hairs still look different from their 2D sprite. Below: each 3D shape, what it looks like, the vanilla hairs drawn with it, and the proposed new shapes (2 of the 13 made, as `quiff` and `sweep`; plus `flattop`, `recruit` and `troubadour`, 2026-10-31).

## Current 22 shapes

| 3D shape | Look | Vanilla hairs using it today | Notes |
|---|---|---|---|
| buzz | thin shell over the scalp | Shaved, RoughShave, PatchyShave | matches Shaved (reads a bit like a tight cap, as noted in the 10-14 log). |
| balding | bare crown, ring of hair round the sides and back | Elder, Scatman, Stephen | good match for Elder and Scatman; Stephen's swept-back mane (east) is not covered. |
| crest | one bold strip from brow over the crown | Rookie, Tuft, DoubleTuft, Primal | good match for Rookie; ShavedMohawk moves here (low strip on a shaved head). |
| mohawk | tall fin from brow to nape | Mohawk, ShavedMohawk | good (old generic model; fin is thinner than the sprite's block). |
| topknot | round knot on the crown, shaved scalp | ScorpionTail, ShaveTopBraid | knot matches, ShaveTopBraid's braid down the back is missing. |
| spiky | short cap with spikes on top | Spikes, SideSpikes, Firestarter, LargeTuft | weak (old generic model). Nearest is LargeTuft; SideSpikes' sideways sweep is not matched. FLAG. |
| afro | big round puff | Afro | good (old generic model). |
| short | close round cap, fringe straight across to the brows, ears showing | Fringe | built from Fringe (2026-10-31, one shell). |
| flattop | flat-topped block, wavy hairline with a V peak at the brow, ears showing | Topdog | built from Topdog (2026-10-31). |
| quiff | close sides and back, a big rounded mass rising over the front of the crown, ears showing | Bravo, ShortCut, Burgundy, GreasySwoop | prototype Bravo (2026-10-31); a little tall for ShortCut. |
| recruit | hair on top only, level hairline above the ears, curl over the right brow | Recruit | built from Recruit (2026-10-31); back and lower sides bare. |
| sweep | close cap, fringe swept across, a lock falling straight down on the left in front of the ear | Revolt, Warden | prototype Revolt (2026-10-31); Warden's ragged ends not modelled. |
| troubadour | two-lobed heart pompadour, a V down the back to the nape, slicked sides bare | Troubadour | built from Troubadour (2026-10-31); comb stripes not modelled. |
| medium | full cap with a fringe, sides over the ears | Mop, Bowlcut, Lackland, Decent, Bob, Messy, Randy, Parted, SideCut | fair for Mop (old generic). Sides reach the cheeks: FACE-SLIM. |
| long | straight hair to the shoulders, centre part | Long, LongOily, LongMess, Wavy, Princess | fair for Long (old generic). Sides past the jaw: FACE-SLIM. |
| shoulder | full parted hair to the shoulders, flared ends | Snazzy, Sticky, Scrapper, Locks, Rockstar, Savage, Junkie | good (built from Snazzy/Locks/Scrapper). Sides past the jaw: FACE-SLIM. |
| bob | straight bob to the jaw, flat fringe | Curly, Flowy, Frozen, Keeper, Cleopatra, Artist, Mary | good for Cleopatra/Keeper. Sides to the jaw: FACE-SLIM. |
| twintails | two tails hanging at the sides | Ponytails, Pigtails | good (built from Ponytails/Pigtails). |
| sidebuns | cap with a round bun over each ear | Henry, Richard, Lisa, Cute (Jane now has its own model) | good for Henry/Richard/Lisa. Buns at cheek height: check gap after FACE-SLIM. |
| topbun_tail | cap with top bun and back tail | Victoria, Senorita | good (Senorita's front braids not modelled). |
| bun | cap with a bun on the crown | FancyBun, Anne, Elisabeth | fair (old generic; bun smaller than Anne's). |
| ponytail | close cap, ears showing, a compact knot low on the back of the head | BriefPonytail, BraidedKnot, William | remade 2026-10-31 as one shell; William's longer hanging tail is not modelled. |

## Proposed new shapes (one vanilla prototype each; MADE = in the table above)

| New shape | Prototype | Look | Vanilla hairs that would move to it |
|---|---|---|---|
| bowl | Bowlcut | rounded helmet with a straight fringe line across the brow | Bowlcut |
| shaggy | Messy | full cap with a ragged, jagged outline all round | Junkie, Messy |
| swoop | GreasySwoop | short sides, big quiff rolled up and forward over the brow | MADE as `quiff` (prototype Bravo); Troubadour got its own `troubadour` |
| sweep | Revolt | short cap with a long fringe swept across to one side | MADE (`sweep`: Revolt, Warden) |
| sidecut | SideCut | one side shaved, long sweep hanging down the other side to the jaw | Scrapper, Savage, SideCut |
| longmess | LongMess | long hanging hair with ragged bangs over the brow | LongOily, LongMess |
| curly | Curly | lumpy curly mass to the jaw, wider than the head | Curly, Frozen, Wavy |
| bell | Artist | very wide bell bob flaring out at the jaw, straight fringe | Artist |
| twinbuns | Cute | cap with fringe and two buns on top of the head | Cute |
| spikes | Spikes | bare scalp with a few tall liberty spikes | Spikes |
| tuft | Tuft | shaved scalp, one star-shaped tuft on the crown | Tuft, DoubleTuft, Firestarter |
| backmane | Primal | shaved front, long curtain of hair from the crown down the back | Primal |
| braid | ScorpionTail | shaved scalp, thick braid from the crown down the back | ScorpionTail |

Notes: FACE-SLIM = hair hanging past the jaw needs a slimmer face; HAT = parts above the hat line are hidden under headgear (IsoRim tucks hair inside hats).

Hair models live in IsoRim's `Models/` folder (person head/hair .obj files); the hair kind for each vanilla hair is chosen in code (HairParts.cs), so a separate addon can replace hair through the figure API (hide `Hair`, attach your own model to the head).
