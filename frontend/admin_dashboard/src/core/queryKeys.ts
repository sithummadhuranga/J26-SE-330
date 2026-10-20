/** The signed-in clinician's own profile (GET /v1/me). The profile panel and My account both use it. */
export const meKey = ['me'];

/** The facility's clinicians. The clinicians page and My account both use it, and a rename refreshes it. */
export const cliniciansKey = ['clinicians'];

/** Your own active sessions (GET /v1/me/sessions), on My account. */
export const sessionsKey = ['me', 'sessions'];
