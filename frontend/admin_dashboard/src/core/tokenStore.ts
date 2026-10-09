// Holds the refresh token in sessionStorage, so it's gone when the tab closes (ADR 0005).
export interface TokenStore {
  read(key: string): string | null;
  write(key: string, value: string): void;
  delete(key: string): void;
}

export const sessionStorageStore: TokenStore = {
  read: (key) => sessionStorage.getItem(key),
  write: (key, value) => sessionStorage.setItem(key, value),
  delete: (key) => sessionStorage.removeItem(key),
};

/** Keeps tokens in memory instead (used by the tests). */
export class MemoryTokenStore implements TokenStore {
  readonly values = new Map<string, string>();
  read = (key: string) => this.values.get(key) ?? null;
  write = (key: string, value: string) => void this.values.set(key, value);
  delete = (key: string) => void this.values.delete(key);
}
