/** Shared capabilities select deployment behavior; session mode alone does not identify Lite. */
export interface SystemCapabilities {
  deploymentMode: 'Full' | 'Lite';
  integrations: { search: boolean; liveChat: boolean; potProvider: boolean };
  accessManagement: { enabled: boolean };
  backups: {
    provider: string;
    full: boolean;
    differential: boolean;
    incremental: boolean;
    verification: boolean;
    pointInTimeRecovery: boolean;
  };
}

let lite = false;

export function configureFrontendAccess(capabilities: SystemCapabilities): void {
  lite = capabilities.deploymentMode === 'Lite';
}

/** Permission decisions never restrict Lite; Full retains the supplied decision. */
export function hasFrontendPermission(granted: boolean): boolean {
  return lite || granted;
}

export function requiresLogin(status: number): boolean {
  return !lite && status === 401;
}

export function usesExternalAuthentication(): boolean {
  return !lite;
}
