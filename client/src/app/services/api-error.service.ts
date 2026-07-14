import { Injectable } from '@angular/core';

/**
 * App-wide mapping of HTTP/API failures to actionable Slovak messages.
 * The audit found a dozen bare "… zlyhalo." strings and several silently
 * swallowed errors — every catch/error callback should go through here:
 *
 *   catch (e) { this.error.set(this.apiError.friendly(e, 'Uloženie zlyhalo')); }
 *
 * Rules:
 *  - A plain-string server response wins (the API returns curated Slovak
 *    messages, e.g. the upload pipeline's retake instructions).
 *  - Known statuses get a diagnosis + next step.
 *  - The fallback prefix is the caller's context ("Uloženie zlyhalo"),
 *    always suffixed with a next step so no dead-end "zlyhalo." remains.
 */
@Injectable({ providedIn: 'root' })
export class ApiErrorService {
  friendly(e: any, context: string): string {
    if (typeof e?.error === 'string' && e.error.trim().length > 0) return e.error;
    if (typeof e?.error?.error === 'string' && e.error.error.trim().length > 0) return e.error.error;
    switch (e?.status) {
      case 0:   return `${context} — ste offline alebo server nie je dostupný. Skontrolujte pripojenie a skúste znova.`;
      case 400: return `${context} — server odmietol požiadavku. Skontrolujte zadané údaje.`;
      case 401: return 'Boli ste odhlásený — prihláste sa znova.';
      case 403: return `${context} — na túto akciu nemáte oprávnenie.`;
      case 404: return `${context} — záznam už neexistuje. Obnovte stránku.`;
      case 409: return `${context} — koliduje s existujúcim záznamom (duplicita).`;
      case 413: return `${context} — súbor je príliš veľký.`;
      case 429: return `${context} — denný limit služby je vyčerpaný. Skúste zajtra.`;
      case 500: return `${context} — chyba servera. Skúste znova; ak pretrváva, kontaktujte podporu.`;
      case 502:
      case 503: return `${context} — služba je momentálne nedostupná. Skúste o pár minút.`;
      default:  return `${context}. Skúste znova.`;
    }
  }
}
