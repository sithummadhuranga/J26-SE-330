import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// jsdom has no layout, so window.scrollTo only logs "Not implemented"; the shell calls it on every tab change.
window.scrollTo = () => {};

afterEach(() => {
  cleanup();
  globalThis.sessionStorage?.clear();
});
