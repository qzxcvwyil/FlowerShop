# Developer notes

## Solution layout and dependency rule
```
Shared  <-  Server
Shared  <-  UI  <-  Client (host)  ->  Admin (lazy-loaded)   (Admin -> UI, Shared)
Tests -> Server, Client, Shared        E2E -> HTTP only (Playwright)
```
`Shared` has no dependencies (pure C#). `UI` holds reusable components and services. `Client` only wires routing, layout and public pages. `Admin` is a separate assembly downloaded on the first visit to `/admin`.

## Conventions
- **Business rules are pure functions in `Shared/Logic.cs`** (pricing, validation, cart, order workflow). No `DateTime.Now`, I/O or DI there. Add a unit test next to every new rule.
- Expected failures return `Result<T>`; exceptions are for bugs and infrastructure errors (the global handler turns them into ProblemDetails 500).
- DTOs are `record`s; change them with `with`, never mutate.
- Components take data via `[Parameter]` and report with `EventCallback`; shared state goes through `CartState` (cascading value) or a scoped service.
- Server is the source of truth: prices, stock and status transitions are recomputed/validated in the API even if the UI already checked them.

## How to add a feature (example: a new order status)
1. Add the value to `OrderStatus` and the allowed moves to `OrderWorkflow.Next` (`Shared/Logic.cs`).
2. Add rows to `OrderWorkflowTests` (`tests/FlowerShop.Tests/WorkflowTests.cs`).
3. Add a CSS class `.s-<name>` in `app.css` and a label in `StatusBadge.razor` if needed.
4. The API and the admin dropdown pick the new rule up automatically.

## How to add an endpoint
Add it in `src/FlowerShop.Server/Program.cs` inside the `/api` group, `RequireAuthorization("Admin")` if needed, return `Problem(detail, code)` for errors, add a wrapper in `ApiClient`, and an integration test in `tests/FlowerShop.Tests`.

## Key decisions
| Decision | Reason |
|---|---|
| Blazor WebAssembly + ASP.NET Core minimal API | one language end to end; shared validation/pricing code; static client works offline as a PWA |
| SQLite + EF Core | zero setup for the demo; provider can be swapped to PostgreSQL |
| JWT in `localStorage` | simple SPA flow; short lifetime (8 h); a cookie-based flow would need CSRF protection |
| Stock as EF concurrency token | prevents overselling without explicit locks |
| Order status as a state machine | prevents illegal jumps (Pending → Delivered) and keeps stock consistent on cancel |
| Admin as a separate lazy-loaded assembly | customers never download admin code |
| No CSP header | Blazor WebAssembly needs `unsafe-eval`/wasm; the other security headers are set |

## Test strategy
| Layer | Project | Notes |
|---|---|---|
| Unit | `FlowerShop.Tests` | pure functions, JWT parsing, hashing |
| Integration | `FlowerShop.Tests` (`WebApplicationFactory`) | real pipeline + temp SQLite file |
| Component | `FlowerShop.Tests` (bUnit) | pages and shared components with a fake HTTP handler |
| E2E | `FlowerShop.E2E` (Playwright) | needs the server running; Chromium/Firefox/WebKit + devices |

## CI and deployment
`.github/workflows/ci.yml`: unit tests on Windows/macOS/Linux with coverage, E2E on Linux (all browsers).
`Dockerfile` + `docker-compose.yml` build and run the published server; `GET /health` is the liveness probe.
