/** The signed-in clinician's own profile (GET /v1/me): the profile panel and My account share it. */
export const meKey = ['me'];

/** The facility's clinicians: the clinicians page and My account share it; a rename refreshes it. */
export const cliniciansKey = ['clinicians'];

/** Your own active sessions (GET /v1/me/sessions), on My account. */
export const sessionsKey = ['me', 'sessions'];
