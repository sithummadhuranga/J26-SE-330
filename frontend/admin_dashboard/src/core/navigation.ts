/** Leaves the app for another page (the identity service's sign-in); tests replace go() to watch where it goes. */
export const browser = {
  go(url: string): void {
    window.location.assign(url);
  },
};
