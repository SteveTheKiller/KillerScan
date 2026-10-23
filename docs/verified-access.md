# Verified access methods

Right-click a device or service and choose **Access**. The read-only list shows
the protocol, numeric host, port, address, observation time and available evidence
about authentication. **Open** uses the existing browser, SSH or RDP actions;
**Copy address** copies that entry. Viewing the list never starts a probe or a login.
Unknown TCP services are copy-only. The service view and its existing browser
action also use captured HTTP/HTTPS schemes on nonstandard ports.

## What the labels establish

- **HTTP response received**: a response was received at the displayed root URL.
  The status code is retained, even without a title or Server header.
- **SSH banner received**: the existing SSH fingerprint supplied a banner. This
  is not a successful login.
- **TCP port open; protocol suggested by port**: the port scanner connected,
  but the service protocol remains a suggestion based on its port number.
- **Address suggested by port**: old or supplied fields have no stored response
  verification. No live observation time is invented.

HTTP 401 is labelled **Server requested authentication**, with the authentication
scheme from WWW-Authenticate if present. A password input inside a form is labelled
**Password form detected**. Otherwise authentication is **Unknown**: HTTP 200,
403, an optional challenge on 200 and an open SSH/RDP port do not prove that login
is or is not required. Comments and script/style blocks are excluded from the
simple form detector. JavaScript-generated login flows, SSO, custom fields and
other application-specific authentication may remain unknown. Evidence applies
only to the requested resource, not every page or the device as a whole.

## Collection and limits

The existing HTTP option controls collection. Quick scans do not probe web
services. Normal scans try the known open web ports (80, 8080, 5000, 8123, 443,
8443, 8006, 5001, 5357 and 32400). Deep mode additionally tries HTTP and HTTPS on
up to 12 other open ports, ordered by the existing TLS-port hints. Every address
shown as an HTTP response uses the protocol that actually returned a response.
After a response, an unnecessary second protocol attempt is skipped, except for
HTTP 400/426 where the server may be rejecting the wrong transport.
Only `/` is requested; paths are not guessed and redirects are not followed.

Collection now retains multiple web endpoints instead of stopping after the first
useful fingerprint. This can take longer, especially during deep scans. Response
capture is bounded to 64 KiB; HTTP attempts have existing connect/header timeouts
and bounded body reads. Interface-bound collection retains its source binding,
target checks and cancellation. Both paths disable redirect following; the
unbound path also explicitly disables proxies and cookies. Neither path sends
login credentials. The first useful legacy HTTP title/Server fields remain
available for classification.

HTTP 401 challenges retain only the scheme, not realms, credentials, cookies,
password values, full pages or redirect URLs. HTTPS detection continues the
existing permissive fingerprint handshake: it does **not** validate certificate
trust or server identity. Open starts a new connection in the selected client,
which applies its own authentication and certificate policy.

## Persistence and compatibility

`NetworkDevice.Access` is an optional list of immutable `AccessEndpoint` records:
Protocol, Port, Verification, Authentication, HttpStatus, Challenge, Redirect and
ObservedAtUtc. Stable language-independent values are used in JSON. The host's
numeric address and the endpoint form the displayed URL; remote redirect targets
are never used as launch commands. Allowed schemes and ports are validated before
opening. SSH uses the existing username prompt and operating-system client.

Normal scanner completion materializes the list, including TCP/SSH provenance.
For port-only evidence, the time is the completion of the host's scan; for HTTP it
is the response observation time. Imported data without times stays undated.
History keeps a separate list snapshot, and older history needs no migration.
Locations retain their existing storage boundaries.

CLI JSON and PowerShell include Access. Device/service/history CSV adds an
**Access JSON** column; consumers relying on fixed column counts must account for
it. HTML includes expandable, escaped access details. UI labels are included in
all 19 supported languages. No passwords or new authentication settings are stored.

## Validation

Engine fixtures exercise HTTP and HTTPS on ephemeral loopback ports, multiple
endpoints, 401, 200 password forms, unknown 200/403 state, redirect refusal and
safe address construction. Bound HTTP fixtures check endpoint-specific evidence.
Offscreen UI fixtures cover every locale, explicit Open/Copy callbacks, nonstandard
service routing, independent history and exports. Demo data is fabricated, with
no invented observation timestamps; it does not replace a physical network pilot.
