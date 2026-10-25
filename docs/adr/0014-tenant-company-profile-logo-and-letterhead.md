# ADR-0014: Tenant company profile, logo and document letterhead

- Status: Accepted
- Date: 2026-10-23

## Context

The product owner asked that a company can see and change **its own** company data in its own administration ("only
their own"), and change its logo. Until now only the platform administration (ADR-0013) could edit master data. Tenant
documents (POS invoices, Ankaufbeleg, till receipts, public e-receipts) carried name, address and VAT ID only — no logo,
no bank details, no managing director / commercial register (HGB §37a / GmbHG §35a "Geschäftsbriefe"), no shop texts
such as opening hours or the return policy. A parallel workstream (Ankauf) needs to embed customer signatures in PDFs.

## Decision

1. **Profile endpoints, always the request's tenant.** `GET /api/organization/profile` (any member, `RequireTenant`) and
   `PUT /api/organization/profile` (`RequirePermission(organization.manage)`, i.e. owners). There is no organization id in
   the route; the service reads and writes `ITenantContext.RequiredOrganizationId`. Editable: display name, legal name,
   address, country, VAT ID, tax number, contact email/phone, website, receipt header/footer (≤ 500 / 1000 characters,
   ≤ 12 lines), bank name / IBAN / BIC, managing director, register court / number, default payment terms (0–365 days).
   Business types and status stay with the platform administration.
2. **Rules in the domain, shape in the database.** `CompanyProfileRules` (Tenancy domain): IBAN = ISO 13616 shape, SEPA
   country length and mod-97 checksum; BIC 8/11; German VAT ID `DE` + 9 digits (other countries: prefix + 2–12 alphanumerics,
   no VIES lookup yet); IBAN required when bank name or BIC is given. FluentValidation reports the same per field (422
   `validation.failed`); check constraints guard IBAN/BIC shape, payment terms range and "bank data needs an IBAN".
3. **Optimistic concurrency** with `version` (= `xmin`), required on PUT; 409 `concurrency` on a stale form.
4. **Audit and admin visibility.** Every change writes an explicit audit entry `TenantProfileUpdate` with
   `{Source: "tenant", Before, After, LegalFieldsChanged}` next to the automatic entity entry. A change of the **legal
   identity** (legal name, VAT ID) needs no re-verification for now, but adds an entry of kind `LegalDetailsChanged` to
   `tenancy.organization_status_changes` (from = to = current status, reason lists old → new values, `changed_by` = the
   owner). The admin company detail shows it as "Vom Unternehmen geändert" (new column `kind`, check constraint).
5. **RLS on organizations for writes.** `tenancy.organizations` gets RLS: everyone may read (as before — names of other
   companies appear in the marketplace), insert stays open (registration), **UPDATE only when no tenant is set (platform
   administration, registration) or the row is the current tenant** (`id = infra.current_org()`), no DELETE policy. A
   forgotten `WHERE` in a tenant request therefore cannot change another company (proven by a mutation run: with the
   tenant filter removed the tests fail and the foreign UPDATE matches no row → 409).
6. **Logo.** `tenancy.organization_logos` (tenant-owned, RLS: read all, insert/update only the own organization, no DELETE
   grant; one active logo per organization via a partial unique index; a trigger allows only the one-time removal).
   Upload `POST /api/organization/logo` (multipart `file`, ≤ 2 MB, ≥ 16 px per side), identified by magic bytes,
   decoded and re-encoded from raw pixels by the shared `IImageCodec` (below): a **screen PNG** (≤ 480 px, transparency
   kept) for the app, the POS and till receipts, and a **print JPEG** (≤ 1200 px, flattened on white, q90) for PDFs.
   Metadata (EXIF/GPS, XMP, ICC) and trailing bytes do not survive. Replacing marks the previous row removed in the same
   transaction; files of the replaced/removed logo are deleted after the commit (orphans possible on a crash, never a row
   without files — as ADR-0011). `DELETE /api/organization/logo` removes it.
7. **Serving logos publicly by token.** `GET /api/logos/{id}/screen|print` is anonymous because the logo is printed on
   public e-receipts and is not confidential. The id is a random UUIDv4 (122 random bits) and acts as the token; content
   behind an id never changes → `Cache-Control: public, max-age=31536000, immutable`, strong ETag (304), `nosniff`;
   removed logos are 404.
8. **Shared image codec.** `IImageCodec` (SharedKernel `Imaging`) with `SkiaImageCodec` in `HandyNet.FileStorage`
   (registered by `AddHandyNetFileStorage`): `Render(bytes, limits, specs)` for several renditions from one decode and
   `ToJpeg(bytes, limits, maxSide, quality)` for PDF embedding. Same pipeline as the product images (ADR-0011); the
   Catalog keeps its own processor for now (migrating it onto the codec is a refactoring without behaviour change).
9. **PDF images (`SimplePdf`).** JPEG only, embedded unchanged as `/XObject /Image /Filter /DCTDecode` (gray/RGB/CMYK,
   size read from the SOF marker; anything else → `ArgumentException`, convert with `IImageCodec.ToJpeg`). The same byte
   array is embedded once per document. API for other modules (e.g. Ankauf signatures):
   - `pdf.Image(jpeg, x, y, width, height)` — absolute, top-left corner measured from the page's top-left, points; the
     text cursor does not move;
   - `pdf.ImageBlock(jpeg, maxWidth, maxHeight, align, indent)` — in the flow at the cursor, aspect kept, page break if
     needed, cursor moves below the image (e.g. `pdf.ImageBlock(signature, 180, 60).Rules(0.5, (0, 200))`);
   - `SimplePdf.FitImage` / `SimplePdf.JpegSize` for custom layouts;
   - `FooterLines` (init): small letterhead lines on every page above the page footer; content stops above them.
   Without images the output stays plain ASCII; with images the file is binary (marked by the `%âãÏÓ` comment).
10. **Letterhead on tenant documents.** Tenancy contract `IOrganizationBranding`: `GetDocumentBrandingAsync` returns a
    `DocumentLetterhead` (logo JPEG from storage + German footer lines: issuer and contact; management, register, VAT ID,
    tax number; bank details) and the payment terms; `GetReceiptBrandingAsync` returns the logo URL and receipt texts.
    POS invoice PDFs and the Ankaufbeleg draw the logo top right and the footer lines on every page. The public
    e-receipt response gets `branding` (logo URL, header, footer — no bank data); `pos.public_receipt` now also returns
    the organization id. The web app shows the logo in the back-office top bar, the POS top bar, the dashboard header and
    on receipts (screen and 80 mm print in grayscale).
11. **Branding is rendered at display time, not snapshotted.** Receipt and invoice documents stay immutable and keep
    their legally required content (seller name, address, VAT ID) as issued. Logo, receipt texts and letterhead footer
    come from the **current** profile when a receipt is shown or a PDF is rendered. A reprint after a change of bank
    details shows the new ones.

## Consequences

- Owners maintain their data without the platform operator; the operator still sees legal-identity changes and every
  change in the audit log.
- Re-rendered PDFs of old invoices can differ in logo/footer from the first download. When archiving issued PDFs (GoBD,
  roadmap) is built, the letterhead must be frozen with the archived file (or snapshotted into the document).
- Payment terms are stored and exposed through `IOrganizationBranding` for the sales invoices of the POS workstream; POS
  invoices for till sales are already paid and do not print them.
- VAT IDs are only checked by format; a VIES check and a re-verification workflow for legal-identity changes are future
  work.
