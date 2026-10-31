# Customer journey and Penpot specification

Status: design draft, 31 October 2026. These screens and APIs are not implemented. No Penpot file has been created.

## Fictional Program

Use `CJora Demo`, Program key `cjora-demo`. Use fictional customer Alex Demo and `alex@example.test`. Branding is presentation only and never determines authorization. Currency, limits and amounts on transfer screens are placeholders pending the accounting contract.

## Frames and shared components

Create phone frames at 390 x 844, then check 360 x 800 and 430 x 932. Add tablet 768 x 1024 and desktop 1440 x 900 variants. Use a 4 px spacing scale, 16 px body text, explicit labels, visible keyboard focus, and touch controls at least 44 px high. Use semantic status text alongside color. Allow text resizing and long names without clipping.

Suggested theme: navy #142D4E, teal #007D76, white #FFFFFF, surface #F4F7FA, text #172B4D. Verify contrast in the design tool before approval. Components: Program header, labeled input, primary/secondary button, alert, skeleton, empty state, amount display, transaction row, status badge, review summary, confirmation dialog, navigation.

Phone shell: Program header, page title, single-column content, primary action after content, bottom navigation when authenticated. Desktop shell: side navigation and bounded content width. Verification uses the selected provider's accessible UI and theme capabilities.

## Journey

Login -> provider verification -> dashboard -> account -> transfer entry -> review -> result -> history -> transaction detail.

The first implementation milestone ends at authenticated dashboard/profile and logout. Account balances, transfers and history are later design scope; never display invented live balances.

| Frame | Main content and actions | States and transitions |
| --- | --- | --- |
| 01 Login | Program identity, email label/input, Continue, privacy/help links | Empty; malformed email; submitting; generic unavailable message. Continue starts provider flow. Do not disclose membership or account existence. Preserve only approved return paths. |
| 02 Verification | Provider-hosted identity verification; cancel/back | Provider selects password, MFA or other verification. Cover incorrect/expired challenge, retry limits, cancelled flow and provider outage. No custom OTP protocol. Return through approved Gateway/Core callback. |
| 03 Dashboard | Welcome, Program name, profile action, logout; later account summary and recent activity | Loading; profile ready; no accounts; service unavailable; expired/revoked session. First milestone shows profile and a clear unavailable state for future financial features. |
| 04 Profile | Display name, masked or approved email, customer reference, Program | Loading; ready; denied; failed load. Read only in first milestone. No numeric database IDs. |
| 05 Account | Account label, currency, posted/held/available amounts, transfer action | Loading; empty; restricted account; unavailable. Amount semantics require ledger agreement. |
| 06 Transfer entry | Source account, recipient, amount, reference, Continue | Required/invalid input; recipient unavailable; insufficient funds; restricted source; expired session. Show currency explicitly. Validation does not reserve funds. |
| 07 Review | Source, recipient, amount, fees, total, reference; Edit; Confirm once | Loading validation/quote; ready; expired quote; changed balance; step-up required; submitting. Prevent accidental duplicate UI submission; backend idempotency remains required. |
| 08 Result | Operation reference, status, amount, recipient, timestamp; View activity | Succeeded; pending/unknown; definitively failed. Pending text: “We are checking the transfer status.” Refresh status using the original operation; do not offer another submission for an unknown outcome. |
| 09 History | Activity list, filters, next-page action, status labels | Loading; empty; filtered empty; error; ready. Preserve filters when returning from detail. |
| 10 Transaction detail | Reference, state, timeline, amount/currency, approved account information | Loading; ready; unavailable/not found; pending. Link reversals without rewriting original activity. |
| 11 Logout | Confirmation if needed; signed-out destination | Submitting; complete; partial server outage. Immediately deny local application access; show retryable logout status until server revocation is confirmed. Never claim global identity-provider logout unless confirmed. |

## Session interruption

On 401, clear protected UI data and return to login with a safe explanation. Preserve no sensitive form contents in URLs or browser storage. On 403, show access denied without triggering a login loop. An expired session during confirmation must not auto-submit a transfer after reauthentication; resolve any previous operation first and require a new explicit confirmation when appropriate.

## Penpot handoff checklist

- Create reusable components and theme tokens, then frames 01-11 and their state variants.
- Connect normal journey, cancellation, session expiry, pending status and logout paths.
- Annotate provider-owned verification and future financial screens.
- Check keyboard order, screen-reader labels, validation announcements, contrast and responsive overflow.
- Review designs against `FIRST-MILESTONE-CONTRACTS.md` before implementation.
