# Hand Viewer — plan & tracker

A second feature in the Spire2PileViewer mod: see a teammate's hand, drawn *behind* your own hand like the
game's after-end-turn hand (slight shadow, mostly occluded), with native card rendering, live energy costs and
native-style hover closeups. Living notes so work can resume after an interruption. Newest status first.

## Milestones

Tags are `hvN` (Pile Viewer uses `mN`). Each milestone ends in an annotated tag so testing can roll back.

| # | Scope | State | Tag |
|---|---|---|---|
| 1 | Dev environment for autonomous testing & review: `scripts/test-settings.ps1` (windowed test settings, applied by every launch script), `-autotestsuite hands` + native-hand probe, this tracker | done | `hv1` |
| 2 | UI: 4th "Hand" slot (eye icon, hand count) in each strip, order draw/hand/exhaust/discard → toggles that teammate's hand view (one at a time, slot highlighted); "Hide hand" button under End Turn; F1 menu tabs (Pile Viewer / Hand Viewer) with: enable Hand Viewer, enable Hide hand button, teammate hand shadow, "% of card visible" (default 30 %). Verified 3 players | done | `hv2` |
| 3 | Live data: text readout ("The Silent — Hand (7): Defend [1] · …", live energy costs) above your hand while a teammate's hand is viewed. Verified over the network: mutator `draw 2` / `card X` / `remove_card X` → observer saw 7→9→10→9 within ~1 s, matching the mutator's own hand exactly | done | `hv3` |
| 4 | Teammate's cards drawn as native `NHandCardHolder`/`NCard`s in a layer just before `NPlayerHand.CardHolderContainer` (behind your hand), native fan layout for their hand size raised by "% of card visible" × card height, gray shadow tint; visuals refreshed 4×/s (the game only refreshes cards it finds via `NCard.FindOnTable`); your Hand Card Count labels lifted by the same amount. Verified live 7→9→10→9 re-fans | done | `hv4` |
| 5 | Closeups: hovering a teammate card (its holder's own hitbox, `HolderFocused`/`Unfocused`) gives the native closeup from `NPlayerHand.RefreshLayout` — upright, scale 1, lifted to y = −hitbox/2+2, ZIndex 1 above your hand, neighbours slide ≤100 px, full brightness, native keyword hover tips. Verified via mouse motion through `Input.ParseInputEvent` | done | `hv5` |
| 6 | Accuracy: energy cost and card numbers reflect the teammate's state (Madness, Snecko Eye, Mummified Hand, Spiked Gauntlets, Strength, Dexterity, Vulnerable, Weak, Frail, enchantments like Sharp) | — | `hv6` |
| 7 | Hide-hand: your own hand becomes a shadowed white outline so the teammate's hand is visible | — | `hv7` |
| 8 | 3+ players | — | `hv8` |
| 9 | Hotkeys (TBD — evaluate with and without) | — | `hv9` |

## Dev / test loop

- `scripts/test-settings.ps1 -Mode apply|restore` — windowed 1280×720 + `steam_appid.txt` (game closed).
  Every launch script applies it automatically; run `restore` when testing is over (puts back fullscreen etc.).
- `scripts/dev-autotest-mp.ps1 -Players N -Suite hands` — automated multiplayer session: lobby readies, host
  sends a networked `fight`, the first client is the *mutator* (changes its piles/hand via networked console
  actions), the host is the *observer*. Logs: `%APPDATA%\SlayTheSpire2\spire2pileviewer_{host,1000,…}_log.txt`.
- Suite `hands` (AutoTest.ExerciseHands) grows per milestone; `piles` is the Pile Viewer regression suite.
- Real input is injected with `Input.ParseInputEvent`; screenshots of each window via scratchpad `shot.ps1`
  (only works while the user isn't using the PC).
- Always verify in multiplayer; 3 players when player count matters.

## Native hand facts (sts2 v0.111.0, probe hv1 + decompile)

- `NPlayerHand.Instance` (= `NCombatRoom.Instance.Ui.Hand`): Control at (0,0) size 1920×1080. `ActiveHolders`
  = visible `NHandCardHolder`s. `PeekButton` (`NPeekButton`) at (100,476), 128×128; its `Visuals` texture is the
  **eye icon** `res://images/atlases/ui_atlas.sprites/peek_button.tres`.
- Layout: `HandPosHelper.GetPosition(handSize, index)`, `GetAngle(handSize, index)`, `GetScale(count)`
  (0.8 at 5 cards). Positions are relative to the holders' parent (fan centered horizontally); e.g. 5 cards:
  x = −340, −170, 0, 170, 340; y = 10, −30, −50, −30, 10; angles −8…8°.
- After end turn (`_isDisabled`): hand tweened down 100 px (`_disablePosition`) and tinted `StsColors.gray`
  (`_disableModulate`) — the look the teammate hand should borrow.
- `NHandCardHolder` (scene): `Hitbox` 300×422 (disabled until the hand enables it), `Flash`, `HandIndex`
  label at (−32,−288) = the **Hand Card Count** number (`SetIndexLabel`, visible when
  `SaveManager.Instance.PrefsSave.ShowCardIndices`), `Card` = `NCard`. `Create(NCard, NPlayerHand)`;
  focus → `HolderFocused` event; `NPlayerHand` does the enlarge-on-hover layout.
- `NCard.Create(CardModel)` + `UpdateVisuals(PileType.Hand, CardPreviewMode.Normal)` renders a card from its
  model; the model belongs to its owner, so dynamic numbers/costs come from the owner's state.
  `NCard` children include `EnergyIcon/EnergyLabel`, `StarIcon`, `Enchantment`, `DescriptionLabel`.
- Energy cost: `card.EnergyCost.GetWithModifiers(CostModifiers.All)` (Local = on-card, Global = e.g. Enthralled).
- Teammate hand: `PileType.Hand.GetPile(player).Cards` (works on every client; live).

## Decisions & open questions

- hv4: teammate cards can overlap the End Turn button at high card counts (the native hand does too); revisit in polish.
- hv4: shadow = layer modulate `1 - 0.6 × shadow` (0 → untinted).

- Hand slot order in the strip: draw | **hand** | exhaust | discard (as specified).
- Only one teammate's hand is shown at a time; opening another closes the first.
- Y-occlusion = **"% of card visible"** (user, 2026-10-12): how much of each teammate card peeks out above your hand;
  default 30 % (mostly occluded). The slider is labelled exactly that.
- **Open — hotkeys (hv9):** decide after hv8 with and without.
