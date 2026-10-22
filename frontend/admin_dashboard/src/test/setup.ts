import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

// jsdom doesn't do layout, so window.scrollTo just logs "Not implemented". The shell calls it on every tab change.
window.scrollTo = () => {};

afterEach(() => {
  cleanup();
  globalThis.sessionStorage?.clear();
});
