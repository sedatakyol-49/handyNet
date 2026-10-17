# ADR-0011: File storage and canonical product images

- Status: Accepted
- Date: 2026-10-17

## Context

Phase 2b needs product photos for the marketplace detail dialog, the "with images" filter and later the B2C
storefront. Uploads are untrusted input (polyglot files, decompression bombs, EXIF with GPS positions and device
serial numbers). Files must not live in PostgreSQL, and production runs on Hetzner where S3-compatible object
storage is available.

## Decision

1. **Abstraction.** `IFileStorage` (SharedKernel: put / open / delete by key) with two implementations in the new
   `HandyNet.FileStorage` project, chosen by `FileStorage:Provider`:
   - `Local` (default): files below `FileStorage:Local:RootPath` (default `App_Data/files`; Docker volume
     `/data/files`). Keys are validated (`[A-Za-z0-9._-]` segments, no `..`, no leading slash) and the resolved path
     must stay inside the root. Writes go to a temp file and are moved into place.
   - `S3`: AWS SDK (`AWSSDK.S3`, Apache-2.0) against any S3-compatible endpoint (`ServiceUrl`, `Region`, `Bucket`,
     `KeyPrefix`, `ForcePathStyle`). Credentials only from user-secrets / environment / secret store, or the SDK's
     default credential chain when empty. **Not yet verified against a real bucket** — only its configuration is
     unit-tested.
2. **Images are canonical catalog data** (`catalog.product_images`), like products: one set per product, shared by
   all tenants. Only the organization that created the product (`products.created_by_organization_id`) or a platform
   administrator (admin routes, no tenant header) may upload, reorder or remove. Enforced in the domain
   (`ProductImageRules.CanManage`) **and** by RLS: everyone may read; INSERT/UPDATE only when no tenant is set or the
   current tenant created the product; no DELETE grant.
3. **Processing.** Accept JPEG, PNG, WebP up to 5 MB, identified by magic bytes (never by name or Content-Type).
   SkiaSharp (MIT; ImageSharp was rejected for its license) checks the declared size before decoding (≤ 12 000 px per
   side, ≤ 40 MP), decodes, applies the EXIF orientation to the pixels and re-encodes from raw pixels to WebP: a full
   rendition (longest side ≤ 2048 px, q85) and a thumbnail (≤ 320 px, q80). Nothing of the original file is kept, so
   EXIF/GPS, XMP, ICC profiles and trailing payloads are gone (tested with an EXIF block carrying a marker string).
4. **Ordering.** `position` 0 is the primary image; at most 8 active images (CHECK `position < 8`) and a DEFERRABLE
   unique constraint on `(product_id, position)` so a reorder can swap positions inside one transaction. Removal sets
   `removed_at` and clears the position (history kept) and closes the gap; the files are deleted after the commit.
   Image changes of one product are serialized with `SELECT … FOR UPDATE` on the product.
5. **Serving.** `GET /api/catalog/images/{id}/thumb|full` is **anonymous** so plain `<img>` tags work and the future
   B2C storefront can reuse it. Ids are random (UUIDv7) and the content behind an id never changes, so responses carry
   `Cache-Control: public, max-age=31536000, immutable`, a strong ETag (304 on `If-None-Match`) and `nosniff`.
   Removed images return 404.
6. **Consistency between DB and storage.** Upload writes files first, then commits the row; on a failed commit the
   files are deleted. A crash between the two steps can leave an orphan file, never a row without a file.

## Consequences

- Product images of a product created by one supplier are visible to everyone (intended: canonical catalog). A
  supplier's own marketing photos for *its offer* would need offer-level images (not built).
- Products created by imports/manual entry belong to the creating organization for image purposes; products without a
  creator (platform) can only be illustrated by platform admins.
- Orphan files are possible after crashes; a cleanup job (list keys without an active row) is future work.
- Docker: the Linux native SkiaSharp library comes from `SkiaSharp.NativeAssets.Linux.NoDependencies`; the image
  build has not been run on this machine (no Docker).
