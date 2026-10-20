# Milestone 1: Grand and Tichu decision phase

**Status:** Implemented and tested. This phase tracks both declarations through one configurable window.

## Agreed behavior

- The phase starts when all four players receive their first 8 cards. Their decision clocks start together. The timer is configurable; the proposed initial setting is 60 seconds.
- Each seat has a visible Grand status: **undecided**, **called Grand**, or **declined Grand**. Undecided is distinct from declined. Status changes are visible as they happen, and one seat may remain undecided while another moves on.
- A player calls Grand before taking their ninth card. Taking the ninth card closes that player's Grand option.
- After receiving all 14 cards, a player who did not call Grand can call Tichu. This option stays open until that player's first card is played or the shared decision deadline passes. Passing does not close it.
- Tichu calls are independent of Grand calls at every seat: a player can call Tichu whether a partner or opponent has already called Grand, and can call it after that Grand call.
- Grand calls are independent. Any player may call, including an opponent or the caller's partner; an earlier call does not prevent another call. Each caller's bet is scored according to whether that player finishes first.
- If a player tries to call Grand after their partner has already called, the interface shows a confirmation warning. It is a warning, not a rule restriction; the player may confirm the second call.
- Bot response timing must not reveal the strength of its private hand. The configured bot pace and public events may affect when it responds, but its private hand strength must not affect the delay. The bot must not display a hand-strength estimate during this phase.
- Waiting is ambiguous. Bots must not treat delay alone as proof of a strong or weak hand. Whether bots use timing as a weak signal is deferred to a separate, optional strategy setting.
- A player who has declined may progress to the later deal or pass stage while another player remains undecided. The fact and timing of that progress are public; pass-slot details are specified separately.

## Implemented default to review

If a seat's timer expires while it is still undecided, the implementation defaults to **declined Grand** and lets that player continue to the remaining cards. This avoids an unintended 200-point call and preserves the later Tichu option. Please review this default.

The declaration engine is in [`src/Tichu.Core/GrandDecisionPhase.cs`](src/Tichu.Core/GrandDecisionPhase.cs). It tracks each seat's declarations, the shared deadline, partner-call confirmation, closing Grand on the ninth card, opening Tichu on the fourteenth card, and closing Tichu on the first play. Events must be supplied in chronological order.

[`src/Tichu.Core/TichuDealer.cs`](src/Tichu.Core/TichuDealer.cs) creates seeded 56-card deals and can replay a saved deck order. It exposes the first eight cards and the additional six separately so a later simulator can connect the deal to the declaration window. The simultaneous exchange in [`src/Tichu.Core/CardExchange.cs`](src/Tichu.Core/CardExchange.cs) validates four complete 14-card hands and transfers one selected card to each other seat.

[`src/Tichu.Core/TichuRoundSetup.cs`](src/Tichu.Core/TichuRoundSetup.cs) applies that exchange, derives the opening seat from the post-exchange Mah Jong holder, and creates a `RoundState`. The timed deal/declaration orchestration, bot strategy or response delays, match loop, self-play runner, checkpointing, and GUI are still not implemented.

## References checked

- [dod.gr Tichu rules](https://www.dod.gr/games/tichu/rules/)
- [Tichu rules from the publisher](https://c.tabletopia.com/games/tichu/rules/rules/en)
