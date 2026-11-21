# Tichu self-play and first policy trainer

This project can generate seeded match trajectories or train a small linear policy against the random baseline or a frozen saved policy. The trainer is a first learning milestone, not a neural network or a strong Tichu player. It learns Grand and Tichu calls, card passing, ordinary card-play, and whether/which out-of-turn bomb to use; Dragon recipients still use the baseline policy.

## Run or resume a dataset

From the repository root in PowerShell:

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet run --configuration Release --project .\src\Tichu.SelfPlay\Tichu.SelfPlay.csproj -- --games 1000 --seed 1 --target-score 1000 --output .\selfplay-output
```

`--games` is the total number of matches wanted in that output directory. Stop the process with Ctrl+C and run the same command again to continue. It skips matches already saved. Each completed match is flushed to disk, so an interruption can lose the match currently being played, but not earlier completed matches. On restart, an incomplete final JSONL line is discarded.

Use a new output directory when changing the seed, target score, decision timer, or match-record version. The run metadata is checked on resume so incompatible data is not appended by mistake.

## Train a first policy

From the repository root in PowerShell:

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet run --configuration Release --project .\src\Tichu.SelfPlay\Tichu.SelfPlay.csproj -- --train --games 1000 --seed 1 --target-score 1000 --output .\training-output
```

`--games` is the total target, so rerunning the same command resumes from the saved weights. The trainer writes `policy-checkpoint.json` atomically after every completed match; stopping during a match only loses that in-progress match. It alternates which team uses the learned policy and updates its linear preferences from the match score margin. `--learning-rate` defaults to `0.02`; checkpoint settings are checked on resume. The default training directory is `training-output` if `--output` is omitted.

To continue from a saved policy against a fixed copy of itself, pass its directory with `--opponent-output` and use a new `--output` directory. The challenger starts with the frozen policy's weights; only the challenger is updated after each match. The opponent weights are fingerprinted, so a resumed run cannot silently switch opponents.

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet run --configuration Release --project .\src\Tichu.SelfPlay\Tichu.SelfPlay.csproj -- --train --games 1000 --seed 1 --target-score 1000 --output .\training-vs-frozen --opponent-output .\training-bomb-decision-6000-20261020
```

The checkpoint stores card-play, declaration, and pass weights, reward baseline, completed-match count, win/loss/draw totals, average reward, and run settings. It does not contain full match trajectories. Older 67-, 83-, and 85-feature move policies load with zeroed weights for newer features; checkpoints from before declaration or pass learning load with zeroed weights, and older 23-, 31-, and 37-feature declaration models load by appending zeroed newer state features. Use the generator command above for replayable JSONL game records.

To compare the saved policy with a fixed checkpoint on fresh seeds, use the same option for evaluation. Omit it to compare against random play:

```powershell
$env:PATH = "C:\Program Files\dotnet;$env:PATH"
dotnet run --configuration Release --project .\src\Tichu.SelfPlay\Tichu.SelfPlay.csproj -- --evaluate --games 200 --seed 1 --target-score 1000 --output .\training-vs-frozen --opponent-output .\training-bomb-decision-6000-20261020
```

Evaluation alternates the learned policy between the two teams and reports wins, average score, and average normalized score margin. When either feature-bonus option is supplied, evaluation also captures match trajectories and reports how often the corresponding actions occurred. Training and evaluation win rates are only rough checks; use substantially more matches before drawing conclusions.

For a controlled ablation, evaluation can override either new signal weight without changing the checkpoint. Use the same seed, game count, checkpoint, and frozen opponent for each run; use zero to measure the checkpoint's current behavior and a positive value to encourage the feature:

```powershell
dotnet run --configuration Release --project .\src\Tichu.SelfPlay\Tichu.SelfPlay.csproj -- --evaluate --games 500 --seed 1 --target-score 1000 --output .\training-known-passed-cards-1000-20261119 --opponent-output .\training-vs-frozen-6000-20261020 --partner-card-preserve-bonus 2 --king-ace-control-bonus 0
```

`--partner-card-preserve-bonus` sets a manual move-score bonus for preserving the physical card received from the partner when an equivalent card can be played. It does not decide whether the bot should play that rank. `--king-ace-control-bonus` sets a manual move-score bonus for playing a King single when every unplayed Ace is either in the acting player's hand or known to remain with their partner. Both values must be between -8 and 8 and apply only during evaluation. The older `--partner-card-signal-prior` and `--king-ace-control-prior` option names remain accepted as aliases.

## Read a perspective replay

Print one round from a seat's point of view using the match journal or its containing directory:

```powershell
dotnet run --configuration Release --project .\src\Tichu.SelfPlay\Tichu.SelfPlay.csproj -- --replay .\selfplay-output --game-id 0 --round-index 0 --seat Bottom
```

Game IDs are the zero-based match IDs in `matches.jsonl`; round indexes are zero-based within that match. The trace shows that seat's dealt and post-pass hands, which cards it sent and received (with the sender identified), its hand at each decision, and the chronological public calls, plays, passes, trick collections, hand sizes, and point totals. It hides every other player's hand and all pass choices that were not delivered to the selected seat. The trace records past-trick passes for inspection; the policy observation still does not retain those pass signals across trick resets.

## Inspect a Tichu hand

Use the offline call-plan analyzer to compare candidate hand partitions and sample whether either opponent can beat each lead. Supply the 14 cards after passing and the three cards sent out by the acting seat:

```powershell
dotnet run --configuration Release --project .\src\Tichu.SelfPlay\Tichu.SelfPlay.csproj -- --analyze-tichu-hand "Dragon,Phoenix,K:Star,10:Jade,2:Pagoda,3:Sword,4:Jade,5:Sword,6:Pagoda,7:Star,8:Jade,9:Sword,J:Pagoda,Q:Star" --pass-right A:Jade --pass-partner A:Sword --pass-left A:Pagoda --received-from-partner Dragon --received-from-right K:Star --received-from-left 2:Pagoda
```

Ranks can be written as names or short forms (`A`, `K`, `Q`, `J`, `10`, `2`); suited cards use `Rank:Suit`, and specials use their name. Optional `--received-from-partner`, `--received-from-right`, and `--received-from-left` values must be cards in the hand. The command analyzes the pre-play state; the programmatic analyzer also accepts snapshots with public plays for its independent lead estimates. It samples hidden hands uniformly after assigning the actor's unplayed outgoing cards to their known recipients. It compares the minimum-play partition with candidates up to one play longer, so it can retain separate leads from a pair.

For the pre-play state, the analyzer compares up to four orderings of each of its top two partitions on the same 12 sampled deals. Each order is replayed with two partner models: the random baseline and a caller-support heuristic. That heuristic usually passes over the caller, sheds a low single against a low opposing single, can use an Ace over Q-or-higher from the opponent immediately before the partner, may answer a pair if the caller has at most two cards, and can lead Dog to transfer the lead to a near-out caller. The caller follows the listed order when leading and uses planned combinations to answer opponents; the two opponents use the random baseline. It reports caller-first rate, one-two rate, and average team point margin with the caller's Tichu scored. Treat these as small-sample comparisons between response models, not calibrated call odds: the partner heuristic is deliberately simple, only the caller's Tichu is scored, and received pass sources are not used as evidence about other players' passing habits. Sequence replay does not run for an in-progress snapshot. The analyzer remains diagnostic and does not change Tichu calls.

## Files

- `matches.jsonl`: one complete match record per line, including seeded deck order, declarations, passes, action-time observations and decisions, trick collections, and round scores. Each call record stores the hand at declaration; Tichu records also identify the preceding action index and any pending Mah Jong wish.
- `run.json`: schema and run settings.
- `checkpoint.json`: count and ID of saved matches; rebuilt from the journal when resuming.

Each action record includes the acting player's hand and the public state needed to describe that decision. `DeckOrder` is included to reproduce a match and inspect it; it reveals every player's hidden cards and must not be used as an agent observation or training feature.

Cards are compact integers in the JSONL file: ordinary cards use `suit * 13 + rank - 2` (`Jade` through `Star`, `Two` through `Ace`), followed by `52 = MahJong`, `53 = Phoenix`, `54 = Dragon`, and `55 = Dog`. `CompactCardJsonConverter` can decode these IDs in .NET.

## Current scope

The learned policy uses legal actions generated by the simulator and hand/public-state features. Card-play features include retaining full-house structure, the interaction between a Mah Jong wish and a retained high full house, and whether a wish targets an unplayed card that this seat passed to an opponent. Round snapshots retain public played-card history by seat, so the policy can stop treating that passed card as held after it is played. It can also account for a known passed single that beats a candidate single and for the case where an opponent has one known passed card left after playing the Dragon. The current top's exact owner is available: move and Tichu-call features distinguish the partner, right opponent, and left opponent, and describe the top combination and relevant hand sizes. The acting seat's round context contains its own exact outgoing pass and the exact card it received from its partner and each opponent; it does not contain the partner's other outgoing passes or opponent-to-opponent pass choices. When the same legal combination can use another card of the same rank, the policy preserves the exact card received from its partner and uses an equivalent opponent-passed copy. Passing remains available. If no equivalent combination exists, the received card remains available for that play, including a unique bomb. Other move features can favor a King single when all unplayed Aces are known to be on the acting team's side. A Phoenix lead has its low lead strength; when responding to a single, the legal move strength reflects that it overtakes any single through Ace but not Dragon, and the Dog cannot be answered. Pass features include the recipient, card rank and points, rank groups, same-suit adjacency, straight potential, and the hand left after each exchange choice. The baseline pass heuristic remains a prior while pass choices are trained. Grand calls use the first eight cards before the exchange. Tichu calls use the post-exchange hand; the simulator offers another decision before each public action until that player plays their first card. Tichu features include the visible Mah Jong wish, its rank, how many matching cards remain in hand, the current top owner, and its strength and shape. These features make the distinctions available to training; migrated older checkpoints have zero weights for them. The declaration policy still does not use pass-source information to estimate exit plans or infer opponents' general passing habits. The ordered sequence replay is a separate diagnostic and is not yet connected to the declaration policy.

The perspective replay is for human inspection. It records cross-trick pass history in the transcript but does not make that history available to policy decisions.

No human-game archive has been downloaded or imported. BrettSpielWelt logs are a candidate source: the [`tich_me` project](https://github.com/kalekundert/tich_me) describes monthly log downloads, and a [Tichu-agent thesis](https://pub.tik.ee.ethz.ch/students/2020-FS/SA-2020-19.pdf) reports training from those logs. Current availability and format still need to be checked; that thesis also filtered out straight-bomb and asynchronous-bomb games.
