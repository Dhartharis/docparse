# DocParse backend

Read `../AGENTS.md` if present and `C:\Proyectos\skills\core\index.md` plus relevant backend skills. Follow `ARCHITECTURE.md` for the current backend boundaries and outstanding decisions.

- Keep one deployment organized under `Modules/<Functionality>`.
- Cross-module imports may use only the other module's `Contracts` namespace.
- Each module owns its persistence; never query another module's DbContext or tables from feature code.
- Provider/persistence/HTTP details stay out of application and domain code.
- SQL files under `Infrastructure/PostgreSQL/Migrations` are the authoritative schema history. Add new migrations; never regenerate/edit an applied migration.
- Preserve tenant checks and composite organization foreign keys. Never reintroduce global document/query endpoints.
- Keep development in Docker local. Azure publication is manual; do not provision or deploy without explicit task authorization.
- Run `dotnet build back/DocParse.csproj`, `python tests/check_backend_boundaries.py` and, with the Docker stack available, `python tests/backend_integration.py` from the repository root for relevant backend changes. The integration suite uses a disposable local database and simulated AI.
- Deliver vertical features across database, backend and frontend, with visual review for each feature. This supersedes the earlier backend-first staging.
