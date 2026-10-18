# Standing goal: deliver usable normalization across all imports

This standing objective defines the outcome and execution standards for the normalization implementation. Deliver full-catalog shadow output before completing further small evaluation milestones. Keep the full release scope and existing production approval requirements.

Deliver a working normalization system across every current and future Relisten artist and importer: shared physical venues and locations/history, artist repertoire, cross-artist compositions, people and taper/transferrer credits, performances, ordered setlist occurrences and recording-file contents. Preserve original evidence, stable playback references and reviewed corrections. Full audio lineage and audio analysis remain outside scope.

The next completed milestone is a browsable end-to-end shadow catalog over the complete retained population. Shadow outputs are proposed interpretations stored separately from accepted production data. Start full-population execution with working methods now; implement missing domains in parallel. Every applicable input has an outcome. Unknown information stays unknown, but missing adapters, blanket abstention and error-only domain output remain unfinished work.

Use powerful model assistance in throughput-sized bounded jobs within existing spending authorization. Reuse measured rules/model results and caches. Local classifier training is optional when it does not improve delivery. Independently audit stratified outputs and fix broad failure classes rather than serially preparing small labeled packets. Preserve trustworthy held-out evaluation for production qualification without making it a prerequisite for shadow computation.

Verification serves integrated delivery: run focused behavioral checks for relevant changes, affected regressions at domain integration and a consolidated broad suite at shared checkpoints. Keep formatter checks before commits/pushes. Historical compatibility and publication performance block the affected release operation, not unrelated shadow domains. Do not report test counts, label counts or another plan as the primary completed outcome.

Continue beyond shadow delivery to independently qualified acceptance/routing, incremental refresh, correction/recovery verification, restored-catalog backfill rehearsal and rollout preparation. Production writes and deployment require approval. Do not mark the overall normalization goal complete on delivery of a report or shadow preview.

## Experimental development policy

Judge progress by what a listener can do in the app. Searching `MSG` should open
Madison Square Garden with recordings across artists. Searching a taper's aliases
should find their supported credits and playable recordings together. Song and
composition search should lead to distinct playable performances. Venue rooms,
namesakes and genuinely uncertain identities must remain distinguishable.
Searchable raw rows, comparison cards, test counts and execution receipts do not
by themselves deliver those outcomes. Build and expose the complete user journey;
use checks to protect it and report the working experience first.

Find a good overall normalization approach and a usable foundation that we can improve. This is challenging experimental work. Favor complete features and informative trials over perfecting individual components. A version with visible, understood mistakes can be useful for local inspection and deciding what to build next.

Work in broad iterations: state the approach being tested and what would change the decision, implement the simplest complete path, expose its results, inspect representative successes and failures, then keep, revise or replace the approach. Finish cross-artist person lookup, shared venue lookup, song-to-playable-file search and supported setlist browsing as integrated features. Do not make another two-match improvement the milestone while those features remain unfinished.

Explore promising alternatives with enough context and model capability to answer the question. Try contextual model reasoning, broader evidence acquisition or a different representation when lexical rules and tiny classifiers cannot distinguish the cases. Keep supported facts separate from inferred hypotheses, but allow plausible local proposals with visible reasons and alternatives. Calibrated probabilities, release-quality precision and independent adjudication of every case are not prerequisites for an experimental proposal or a reader Alec can use. Never invent source evidence or present a guess as a verified fact.

Prioritize changes by their effect on feature completion, coverage, recurring errors, architecture or practical runtime. Isolated low-impact errors can remain in a visible known-issues list until the next integration pass. Do not start a tuning or compatibility project for each discovered imperfection. Stop and reconsider a method when repeated changes add machinery without improving the overall result. New evidence that can change the approach is a better reason to experiment than a target of eliminating every last error.

Expose usable local results promptly. Block the affected operation for destructive behavior, lost source/playback references, unusable execution or misleading evidence. Keep other operations and domains available. Known semantic errors and incomplete audits are displayed alongside local results; they do not require withholding the whole reader. Production qualification and deployment remain separate requirements.

Use the minimum verification that establishes the current change's observable contract. Group related fixes, run focused checks, then consolidate integration/regression checks. One pinned implementation and retained inputs normally suffice for an iteration. Add frozen copies, receipts, reviews and historical-state tests only when they resolve a concrete risk or are required by an existing contract. Improve common failure classes; avoid repeated review layers, artifact proliferation and duplicate full runs for small deltas.

Report the working feature, what the broad trial taught us, the largest remaining weakness and the next consequential change. Include an accessible reader and examples when available. Keep test counts, small accuracy deltas and uncertainty notes subordinate to that explanation. The goal is an effective overall approach across imports that we can build on, followed by the work needed to release it.

## Maintainable domain boundaries

Keep the code that applies catalog knowledge small and stable as the knowledge grows. Use provider adapters to turn source formats into typed observations with evidence references. Give each domain a clear resolution path from observations and candidates to proposed matches, alternatives or unresolved reasons. Keep fetching, storage, retries, publication and reader formatting outside pure matching logic. Reuse the same domain resolver in batch, local and runtime paths where they have the same semantics; do not maintain separate sets of matching rules for a preview and the application.

Represent growing aliases, provider vocabulary, reviewed relationships and historical facts as scoped, attributed knowledge where that is their real responsibility. A venue alias needs locality/premises/date context; a song alias needs artist context; a person alias needs supporting evidence. Provider formatting quirks belong with the provider parser. Keep actual domain policy in readable domain code. A small readable constant does not need a configuration system, and unlike domains do not need a universal rule engine or ontology.

Before adding a special case, state the domain principle or source evidence it implements. Keep legitimate scoped knowledge; avoid conditions that recognize a particular evaluation row, incidental filename or test fixture. Use unseen artists/providers/families and hard negatives to check transfer when a method changes. Do not silently rewrite evaluation answers to fit behavior. New examples should mostly extend knowledge, provider extraction or regression evidence rather than growing the orchestration method.

At integrated feature milestones, apply a bounded simplification pass: trace a representative input to output, identify mixed responsibilities, duplicate policy, repeated parsing and obsolete experimental branches, then remove concrete duplication or split a coherent responsibility. Name the problem and target files before editing. An abstraction must own a shared invariant or meaningful behavior; forwarding wrappers, sprawling optional parameters and a generic framework do not establish clean design. Prefer cohesive classes for shared state and behavior, direct data flow and orchestration that names domain phases in order.

Keep this pass proportional and concurrent with delivery. Preserve observable behavior and useful experiments; archive historical experiment outputs rather than making all of them permanent runtime compatibility surfaces. Remove a compatibility path only after establishing that no persisted state or active consumer needs it. Verify changed public contracts with focused tests and inspect the formatted source for readability. The result should make the next provider, alias or domain improvement easier to add, without postponing the current usable feature.

## Implementation and writing standards

Implement the intended outcome, adapting the plan when code or verification reveals a better approach. Follow through on correctness and maintainability issues; explain consequential departures and their trade-offs. Use the repository's existing `docs/` folders for plans, design, research and operational documentation; do not introduce a `personal/alecgorge/` documentation tree. Commit logical chunks as work proceeds, with a title and body that explain the problem, resulting behavior, reasoning and verification.

Favor straightforward, readable code. Apply single-responsibility and DRY principles where they improve clarity. Document public interfaces and explain ownership, constraints and failure behavior where a reader needs that context. Comments should explain intent rather than narrate assignments. Use `code-simplifier` after nontrivial implementation and focused verification; avoid cleanup unrelated to the current change.

Verify observable behavior through stable public interfaces and contract boundaries. Write tests that catch meaningful regressions; avoid tests of private structure, configuration, strings/constants or incidental call order. Apply the delivery plan's verification cadence without dropping checks needed for the behavior being changed.

Write for Alec and for engineers who know Relisten but have not followed this conversation. Lead with the result, explain unfamiliar terms and provide the context needed for the next action. Use natural, self-contained prose and `deslop` throughout. Revise substance and structure before sentences. Remove repetition, drafting history, abandoned approaches, unnecessary contrasts and repeated caveats. State material uncertainty once and connect it to action.

Apply Zinsser, McPhee, Strunk and White, ASD-STE100, Virginia Tufte and Forsyth selectively for clarity, structure, economy, syntax and voice. Use conclusion-first reasoning, context/action/result explanations and progress/plans/problems updates when useful; do not force a template or invent rationale. PR descriptions should explain the final change, relevant verification and operational considerations to a reader outside this thread.

## When Alec's input is needed

Ask promptly when a concrete product decision, missing fact, access, storage or spending decision requires Alec's judgment. Give the relevant evidence, your recommended option and the practical consequence of waiting. Use the available user-input interface and make the pending question visible in the status update. Do not bury it in a report or keep researching around a decision that only Alec can make. Keep routine implementation choices and large labeling jobs with the agent.

Stop work that depends on the answer; continue genuinely independent useful work. Silence is not an answer or approval. Do not repeat the same question, invent assumptions to bypass it, or create extra verification work to keep the goal busy.

Use the goal tool's actual blocked-state contract: when the same blocker has persisted for at least three consecutive goal turns and no meaningful progress is possible without Alec's input or an external change, mark the goal `blocked`, report the returned status and stop goal work. Ask on the first occurrence; the three-turn rule is not a reason to postpone the question or manufacture three attempts. Before that threshold, keep the question explicit and perform only useful independent work. After a blocked goal is resumed, reassess the blocker under the tool's fresh audit rules. Use `paused` only when Alec explicitly asks to pause, and never mark unfinished work complete to escape a blocker.

## Goal tracking

The implementation thread is `01a14720-eb42-71c1-8b3d-84c8eff0c3cd`. It owns the active execution goal. This file is the persistent objective amendment and must be linked from that goal when its interface supports objective edits. If the interface only supports goal status changes, retain the unfinished active goal and apply this amendment to its execution; do not mark an unfinished goal complete just to replace its text. Report the interface limitation accurately.

See [shadow delivery](shadow-delivery.md) for observable milestones and proof. A new status update should identify actual processing/output progress, working provider/domain coverage, the largest blocking implementation gap and the next integration result.
