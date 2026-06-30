declare global {
  namespace App {
    interface PageData {
      config?: AuthConfig | null;
      status?: AuthStatus;
    }
  }
}

export interface AuthConfig {
  mode?: string;
  authority?: string;
  audience?: string;
  singleUserMode?: boolean;
  [key: string]: unknown;
}

export interface AuthStatus {
  singleUserMode: boolean;
  hasSession: boolean;
  accessTokenExpiresAt?: number;
  idTokenClaims?: Record<string, unknown>;
  accessTokenClaims?: Record<string, unknown>;
}

export {};
