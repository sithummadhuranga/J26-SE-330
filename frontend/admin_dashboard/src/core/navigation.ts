/** Leaves the app for another page, like the identity service's sign-in. Tests replace go() to see where it goes. */
export const browser = {
  go(url: string): void {
    window.location.assign(url);
  },
};
