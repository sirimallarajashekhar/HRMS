Review the changes on this branch against main (git diff main...HEAD) as a strict senior .NET reviewer.
Check: CLAUDE.md architecture rules (especially tenant isolation and no cross-module table access), correctness against the FSD, security (authz checks, injection, sensitive data exposure), performance (N+1 queries, missing indexes), test coverage of edge cases, naming and readability.
Output a list of findings ordered by severity with file and line, then fix the critical and high ones after I confirm.
