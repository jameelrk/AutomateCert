# Certificate authorities, CA accounts and DNS providers

> Certificates are issued by the CA you select, under that CA's own subscriber agreement and terms. You remain responsible for CA contracts, fees, domain control and certificate content. Balance and order figures come from CA APIs and can be delayed. Confirm them in the CA portal.

## 1. Three different things
| Object | Purpose | Example |
|---|---|---|
| **Issuer** | Where certificates are *issued*: an ACME directory, or AD CS | "DigiCert OV (ACME)", "Let's Encrypt (free)", "Corp Issuing CA" |
| **CA account** | *Read-only* view of a commercial account: balance, orders, status | DigiCert CertCentral API key, Sectigo SCM admin login |
| **DNS provider** | Creates `_acme-challenge` TXT records for domain validation | Cloudflare, Azure DNS, Windows DNS |

Each automation profile chooses its own issuer, so DigiCert, Sectigo, GlobalSign and Let's Encrypt can run side by side.

## 2. Issuers
| CA | Directory URL | EAB | Notes |
|---|---|---|---|
| **Let's Encrypt** | Pre-configured by Setup (production and staging) | No | Free DV for public names. Staging certificates are not trusted by browsers |
| **DigiCert CertCentral** | Created per credential under *Automation → ACME Directory URLs* | Yes | Choose product, organisation and validity; copy URL, Key ID and HMAC (shown once). Enable automatic request approval. Legacy CertCentral ACME was retired on 24 Feb 2026 |
| **Sectigo SCM** | `https://acme.sectigo.com/v2/DV` or `/v2/OV`, or the URL shown in SCM | Yes | Account linked to an organisation; OV needs pre-validation |
| **GlobalSign Atlas** | `https://emea.acme.atlas.globalsign.com/directory` | Yes | MAC key valid 30 days or 1,000 uses; match the RSA/ECC product |
| **ZeroSSL, Google Trust Services** | See the CA's documentation | Yes | Verify the URL in the CA portal |
| **Private ACME** (step-ca, EJBCA, DigiCert TLM, Sectigo Universal ACME) | Your server | Optional | Agents must trust the private root |
| **Microsoft AD CS** | n/a (certreq on the agent) | n/a | For internal names (`.local`, `.corp`, single-label) |

**Coverage end date (per issuer):** enter the date your paid subscription ends. AutomateCert alerts at 60, 30 and 7 days, and when it has ended.

## 3. CA accounts (balance and status)
| Type | What AutomateCert shows | Credentials |
|---|---|---|
| **DigiCert CertCentral** | Account balance, available funds and currency (Services API *View balance*), orders and upcoming expirations | API key (a *View Only* key is enough); division ID for multi-division accounts |
| **Sectigo SCM** | Certificate counts and recent SSL certificates (Admin API) | Customer URI, admin login and password (use a read-only API admin) |

- **Low-balance alert:** set a threshold per account; an email is sent when available funds drop below it.
- **Sync:** every 6 hours, or **Sync now**.
- Other CAs (GlobalSign, Let's Encrypt) have no balance concept or no public balance API. AutomateCert shows their ACME directory reachability instead.

## 4. DNS providers (DNS-01)
| Provider | Automation | What you need |
|---|---|---|
| Cloudflare | Automatic | API token: Zone → DNS → Edit, plus Zone → Read |
| Azure DNS | Automatic | Service principal with *DNS Zone Contributor* on the zone |
| Amazon Route 53 | Automatic | IAM user with `route53:ChangeResourceRecordSets` / `ListHostedZones` |
| GoDaddy | Automatic | API key and secret |
| RFC 2136 (BIND, others) | Automatic | Server, TSIG key name, algorithm and secret |
| Windows DNS | Automatic, via an agent on a DNS server | An agent on a Windows DNS server with rights to the zone |
| Webhook | Automatic | Your HTTPS endpoint; verify the `X-AutomateCert-Signature` HMAC |
| Manual | You create the record | Email with the exact record, then click **DNS ready** |

**CNAME delegation (split DNS or unsupported registrars):** point `_acme-challenge.app.contoso.com` to a name in a zone AutomateCert controls. The provider screen shows the CNAMEs to create.

## 5. Public vs internal names
Public CAs only issue for registered public domains. AutomateCert blocks internal names (`.local`, `.corp`, `.lan`, `.internal`, `.home.arpa`, single-label names, IP addresses) when the profile uses a public CA, and suggests AD CS. Without this, the CA would reject the order with `rejectedIdentifier`.

## 6. CAA
If a domain publishes CAA records, they must allow the CA: `digicert.com`, `sectigo.com`, `globalsign.com`, `letsencrypt.org`, `pki.goog`. **Diagnose** detects CAA failures.
