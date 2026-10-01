# Open Dental fork guidance

The default `main` branch is a documentation landing branch, not a compilable application checkout. It contains [README.md](README.md), the license, and [MIDDLE_TIER_ARCHITECTURE_ASSESSMENT.md](MIDDLE_TIER_ARCHITECTURE_ASSESSMENT.md). Do not invent build/test commands or add tooling based on absent solution files.

## Choose the exact source version

Upstream stores version families on branches such as `21_4`; a commit identifies the precise build. For code work, first establish the user-requested version branch and commit, then inspect that tree's solution, dependencies, guidance, and tests. Never switch an active checkout or substitute the newest version silently. Preserve upstream license and contribution constraints: the README says upstream does not accept outside code contributions.

## Evidence and clinical boundaries

The middle-tier assessment analyzes `24_3` at `d804c19546233593d8a66af0591ed118a9e2c794`. Its project paths, Windows/.NET dependencies, and suggested hosting changes are historical evidence for that revision, not proof of another branch's architecture or an implemented migration. Revalidate them against the selected source before design changes.

The README requires installing the trial to obtain initial database tables and points to vendor build instructions; this is a prerequisite reference, not permission to install software or initialize a database. Use isolated, non-production environments for compilation and schema investigation. Changes touching patient records, clinical services, database initialization, upgrades, or connectivity require the exact authorized target and rollback/readback plan. Never copy patient data or connection secrets into commits or reports.

For landing-branch documentation changes, validate referenced branch/commit/path claims. No automated build or test gate is present on this default branch; report that limitation clearly.
