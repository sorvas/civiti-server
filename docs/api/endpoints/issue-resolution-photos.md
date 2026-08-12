# Resolution photos ("after" proof)

When an author marks their issue resolved they may attach up to **3 optional photos** showing
that the reported problem was actually fixed. The clients render them against the issue's
original photos as a before/after comparison.

Extends `PUT /api/user/issues/{id}/status` — no new endpoint.

---

## Wire contract

### Request — `UpdateIssueStatusRequest`

```jsonc
{
  "status": "resolved",
  "resolutionPhotoUrls": [            // optional, max 3
    "https://<project>.supabase.co/storage/v1/object/public/issue-photos/<userId>/…jpg"
  ]
}
```

| Rule | Behaviour on violation |
| --- | --- |
| Accepted only with `status: resolved` | `400` — `"Resolution photos can only be attached when resolving an issue"` |
| At most `IssueValidationLimits.MaxResolutionPhotoCount` (3) URLs | `400` — message names the limit |
| Absolute `http`/`https` only, ≤1000 chars | `400` — shared guard with issue photos (`IssuePhotoWriter.ValidateUrls`) |
| Blank/whitespace entries | Dropped silently; they do **not** count against the cap |

All of these are checked **before the transaction opens**, so a rejected request leaves the
issue's status untouched.

The URLs are uploaded client-side to the existing public `issue-photos` Supabase bucket under
the author's own `{userId}/…` prefix, exactly as issue-creation photos are. No new bucket, no
new storage policy — see `docs/technical/photo-upload-specification.md` in the web repo.

### Response — `IssueDetailResponse`

Two additive fields. `photos` keeps its exact previous meaning: the photos of the *problem*.

```jsonc
{
  "resolvedAt": "2026-08-12T09:31:00Z",   // null unless currently Resolved
  "resolutionPhotos": [                    // empty unless currently Resolved
    { "id": "…", "url": "…", "description": null, "createdAt": "…" }
  ]
}
```

`IssueResolutionPhotoResponse` has **no `isPrimary`** — a resolution set has no primary photo.
Order is `DisplayOrder`, then `Id` as a stable tiebreak.

---

## Semantics

**Replace-set.** Every resolve rewrites the whole set. Omitting `resolutionPhotoUrls`, or
sending an empty list, resolves with no proof at all. What is displayed is therefore always what
the *latest* resolve attached — a resolve-with-proof / re-open / resolve-without-proof sequence
cannot leave stale proof on the page.

**Re-opening deletes the set** and clears `ResolvedAt`. So does an admin `request-changes`,
which is the only other route out of `Resolved` — approval and rejection both refuse anything
outside `Submitted`/`UnderReview`. Between them the invariant the clients render on is
structural rather than conventional:

> A resolution photo set exists **if and only if** the issue is currently `Resolved`.

## Moderation

Resolving is owner-driven and takes no approval, so these photos are the one content a user can
put on a live public page without passing back through review. Two things follow:

- `AdminIssueDetailResponse.ResolutionPhotos` surfaces them, so a reported issue is inspectable.
- `request-changes` deletes the set, so an admin can take proof down without waiting for the
  owner.

**Not currently enforced:** the URLs are only checked for scheme and length, not host, so an
author can point them at any `https` origin rather than the platform's own storage bucket. That
makes an unreviewed external request fire for every visitor of the issue page — a tracking
beacon at worst. Restricting the host to the configured Supabase storage origin inside
`IssueResolutionPhotoWriter.Validate` would close it, at the cost of forbidding external image
links outright. Left as a product decision rather than shipped silently.

**`ResolvedAt` is display data**, stamped by the same conditional claim as the status transition
so the two can never disagree. It is deliberately neither of the existing timestamps:

| Field | What it actually is | Why it cannot be displayed |
| --- | --- | --- |
| `ResolutionRewardedAt` | Once-per-issue gamification latch | Never moves after the first resolve |
| `ResolutionNotifiedAt` | Anti-spam fan-out cooldown | Skipped entirely when re-resolving inside the cooldown |
| `UpdatedAt` | Last write of any kind | Moved by any later edit |

---

## Storage model

A separate `IssueResolutionPhotos` table with its own `Issue.ResolutionPhotos` navigation —
**not** a discriminator column on `IssuePhotos`.

`Issue.Photos` is read by roughly fifteen call sites that all mean "the photos of the problem",
several of which corrupt silently if after-photos join the collection:

| Site | What would have broken |
| --- | --- |
| `IssueService.UpdateIssueAsync` (`IssuePhotos.RemoveRange(issue.Photos)`) | An owner edit would **delete** the resolution photos |
| `GamificationService.CheckQualityPhotos` (`i.Photos.Count >= 3`) | Badge criterion inflated and farmable |
| `IssueService` list mappers (`MainPhotoUrl`: `p.IsPrimary \|\| i.Photos.Count == 1`) | Single-original issues would lose their list thumbnail |
| `IssueContentSnapshot` / `IssueSnapshotDiff` | Re-review diffs would report after-photos as content changes |
| `PhotoCount` in the admin queue, MCP tools, `IssueEndpoints` | Miscounted |

Keeping the collections apart leaves every one of those byte-identical, which is why the
separate table won over the one-table option: it makes the separation structural instead of
fifteen filters that must each be written and then never forgotten.

---

## Migration

`20260812083135_AddIssueResolutionPhotos`

- Creates `IssueResolutionPhotos` (`Id`, `IssueId` FK cascade + index, `Url` ≤1000, `Description`
  ≤500, `DisplayOrder` default 0, `CreatedAt`).
- Adds `Issues.ResolvedAt` (nullable `timestamptz`).
- **Backfills** `ResolvedAt = UpdatedAt` for rows already in `Status = 5` (Resolved). Without it
  the entire existing resolved corpus would render a dateless banner. Only `Resolved` rows are
  stamped, preserving the null-unless-resolved contract.

---

## Client compatibility

Purely additive: `photos` is unchanged and the two new response fields are optional on the
clients. A client that does not know about them is unaffected, so the mobile app needs no change
to keep working — it simply does not display proof photos until it opts in.
