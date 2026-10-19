# Relisten.Contracts

The v1 protobuf contracts of the audio-alignment ("reality") pipeline. `proto/` is a verbatim
copy of `proto/` in RelistenNet/string-theory @ `b1af387`, which owns the contracts, their contract
tests and their rules: within `v1` changes are additive only (new fields, new enum values;
never renumber, retype or reuse a field number). A breaking change is a new package (`v2`).

To update, copy `proto/` again from string-theory and note the commit here. Don't edit these
files in this repo.
