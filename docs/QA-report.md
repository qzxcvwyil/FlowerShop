# QA report — FlowerShop

Fill in the **[ ]** and **___** fields after you run each check. Do not tick a box you did not actually perform.

## 1. Automated results
| Suite | Command | Result | Date |
|---|---|---|---|
| Unit + integration + bUnit (90+ tests, incl. order workflow, stock restore on cancel, health, security headers) | `dotnet test tests/FlowerShop.Tests` | passed ___ / failed ___ | ___ |
| Coverage | `dotnet test tests/FlowerShop.Tests --collect:"XPlat Code Coverage"` | line coverage ___ % | ___ |
| Browser E2E (Windows) | `dotnet test tests/FlowerShop.E2E` | total 16 · passed 14 · skipped 2 · failed 0 (2 min 31 s) | 2026-11-17 |
| Browser E2E (Linux / CI, incl. real WebKit) | GitHub Actions `CI` | passed ___ / skipped ___ / failed ___ | ___ |

Skipped on Windows: the two WebKit-only tests (Playwright's Windows WebKit cannot run Blazor WebAssembly). Apple devices are emulated in Chromium there.

## 2. Browser / device matrix
| Environment | Catalog + search | Register → cart → order | Admin live order | Layout (no horizontal scroll) | How verified |
|---|---|---|---|---|---|
| Chromium (desktop) | ✅ | ✅ | ✅ | ✅ | Playwright |
| Firefox (desktop) | ✅ | ✅ | – | ✅ | Playwright |
| WebKit (desktop Safari engine) | [ ] | [ ] | – | [ ] | CI on Linux |
| iPhone 13 profile | ✅ (load, add to cart) | – | – | ✅ | Playwright (Chromium emulation on Windows) |
| iPad Pro 11 profile | ✅ (load, add to cart) | – | – | ✅ | Playwright (Chromium emulation on Windows) |
| Pixel 7 profile | ✅ (load, add to cart) | – | – | ✅ | Playwright |
| **Real phone** (model/OS/browser: ___) | [ ] | [ ] | – | [ ] | manual, `http://<PC-IP>:5000` |
| **Real Safari** (macOS/iOS: ___) | [ ] | [ ] | – | [ ] | manual |

## 3. Manual checklist
- [ ] Register with an invalid email / short password → clear error message
- [ ] Register with an already used email → "Email is already registered"
- [ ] Wrong password at sign in → "Invalid email or password"
- [ ] Add to cart, reload page → cart is restored
- [ ] Sold-out product cannot be added
- [ ] Address shorter than 8 characters is rejected at checkout
- [ ] Order ≥ 15 000 ₸ → delivery 0; below → 1 500 ₸
- [ ] Admin changes status → customer's "My orders" updates without reload
- [ ] Admin dropdown offers only the next valid step (+ Cancelled); Delivered/Cancelled orders are locked
- [ ] Admin cancels an order → product stock goes back up in the catalog
- [ ] Customer opening `/admin` sees "Access denied"
- [ ] Stop the server, click Retry in the catalog → error + retry works after restart
- [ ] OS dark mode → UI switches theme, text readable
- [ ] Keyboard only: Tab reaches skip-link, menu, Add buttons; focus visible
- [ ] Rotate phone to landscape → layout adapts

## 4. Lighthouse (Chrome DevTools, incognito, production build)
| Page | Performance | Accessibility | Best Practices | SEO | PWA |
|---|---|---|---|---|---|
| Catalog | ___ | ___ | ___ | ___ | ___ |

## 5. Issues found and fixed
| # | Issue | Fix |
|---|---|---|
| 1 | E2E tests in WebKit timed out on Windows (Blazor WASM did not start in Playwright's Windows WebKit build) | WebKit-only tests skipped on Windows, Apple devices emulated via Chromium, real WebKit run in Linux CI |
| 2 | ___ | ___ |
