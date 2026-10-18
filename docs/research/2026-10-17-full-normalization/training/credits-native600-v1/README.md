# Native credit extraction: 600-field development comparison

Luna improves taper and transferrer extraction on this development cohort, mainly on free-text notes. It is not yet evidence for automatic global person matching or release-wide accuracy. The two small models, trained on the earlier 84 fields and kept fixed here, performed worse than the rules. No model was fitted or tuned on these 600 labels.

The cohort contains 600 native fields from 600 sources and 302 artists: 300 Archive.org fields from 300 artists, 150 Local fields from one artist and 150 Phish.in fields from one artist. Fields comprise 100 taper, 50 transferrer, 50 description, 50 lineage and 350 taper-notes entries. Labels were source-reviewed and adjudicated by Codex before this comparison; they are exposed development evidence, not independently established human truth or an untouched test set.

## Accuracy and coverage use different denominators

Only 457 fields have complete labels. The other 143 retain known positives and ambiguity; extra predictions on them are not counted as false assertions. The shared comparison contains 343 complete fields: 338 with usable Luna responses and five empty fields requiring no model call. It contains 242 distinct literal name-and-role assertions. Scoring compares each emitted display name exactly with the cited label name, plus role, without name normalization. Source-boundary anchoring is a separate diagnostic. Precision is the share of emitted assertions that are correct; recall is the share of labeled assertions recovered.

| Shared fields | Correct assertions | False assertions | Missed assertions | Precision | Recall |
|---|---:|---:|---:|---:|---:|
| Rules | 178 | 12 | 64 | 93.7% | 73.6% |
| Luna | 226 | 9 | 16 | 96.2% | 93.4% |
| Old-fit logistic model | 99 | 31 | 143 | 76.2% | 40.9% |
| Old-fit perceptron | 92 | 59 | 150 | 60.9% | 38.0% |

The nine Luna false assertions are extraction errors, not nine false canonical-person merges. No identities or accepted credits were published.

For tapers alone, rules reach 93.4% precision and 73.5% recall; Luna reaches 94.7% and 92.3%. For transferrers, rules reach 94.1% and 73.6%; Luna reaches 98.8% and 95.4%. These use the same 343 fields, with 155 taper and 87 transferrer assertions.

An exact field has no missed or extra taper/transferrer assertions. Luna makes 41 previously incorrect fields exact, introduces six new whole-field errors and leaves nine fields incorrect under both methods. Exact fields rise from 293 to 328 of 343. There are 140 fields with no labeled priority-role assertion, so exactness on the 203 positive fields is also useful: 153 become 188 exact. Luna recovers 54 assertions missed by rules, loses six correct rule assertions, removes five false assertions and adds two.

The gain is concentrated. Local and Phish.in contribute 34 of the 41 corrected fields across only two artists. On the 250 shared Archive.org fields, rules find 127 assertions with four false assertions and 12 misses; Luna finds 128 with six false assertions and 11 misses. Taper notes contribute 36 corrected fields. The 49 shared lineage fields contain no labeled taper/transferrer assertions; they cannot establish positive lineage-credit extraction ability.

Across all 457 complete fields, rules find 263 of 416 assertions, with 25 false assertions: 91.3% precision and 63.2% recall. Luna's available-field score still covers only 343 fields and 242 assertions. Comparing those recalls directly would conceal unavailable outputs. The 37 completely labeled fields with no usable Luna result contain 56 assertions; rules recover 25. The 77 completely labeled contact-excluded fields contain another 118 assertions.

## Coverage and next work

All selected fields remain accounted for: 402 usable Luna outputs, 124 email/contact-bearing fields not sent to Luna, 69 fields with no usable Luna result and five empty fields. Raw usable response coverage is 67.0%, not 343/600. Two token-capacity limitations remain in the retained labels without moved spans; both occur on incomplete fields and contribute only positive-recovery diagnostics.

The job records 453 attempts: 384 succeeded and 69 failed, with $0.0518212 accounted token cost. Eighteen output selections were cache hits: 11 reused this job's successful keys; seven reused five keys from other jobs. Historical cache cost is separate. Failure-cause analysis is a separate investigation.

The next comparison should broaden difficult prose across artists and retain unavailable cases in its coverage report. Investigate unusable results and the six new errors before choosing fallback routing. Preserve these labels and outputs unchanged. The current results justify that work; they do not activate an automatic acceptance policy.

## Evidence and reproduction

[evidence.json](evidence.json) retains aggregate provider, field and role results, exact denominators, cost and executable/input pins. The [cohort operator guide](../../../../../tools/RelistenNormalization/luna-credit-cohorts.md) defines acquisition and comparison commands. The [active evaluation milestone](../../../../plans/active/2026-10-17-full-normalization/evaluation-milestone.md) sets the broader work and acceptance requirements.

Full output, labels and audit artifacts stay outside Git. The frozen script uses the original local checkout paths listed in its input pins; those retained inputs must be available for replay. The local [retained audit manifest](../../../../../local-dev/normalization-data/2026-10-18-credit600-comparison-report-v1/manifest.json) has SHA256 `d3c93701eb1866bd04fbcd4a8a84450d221bf538f4187c1711b97e8b2992ca0e`; this link requires the retained local evidence. Reproduce the arithmetic from the repository root in a new scratch directory, preserving the frozen packet:

```sh
audit_output="$(mktemp -d /tmp/relisten-credit600-audit.XXXXXX)"
cp local-dev/normalization-data/2026-10-18-credit600-comparison-report-v1/audit.py "$audit_output/"
python3 -O "$audit_output/audit.py"
```

The script authenticates the retained 600-field plan, labels, native field copy, actual Luna output and frozen model/reference files. It makes no API calls, trains no model and changes no accepted data. Expected metrics and input pins are recorded in the retained manifest and public evidence. A fresh independent math/source review and separate readability review found no remaining issue.
