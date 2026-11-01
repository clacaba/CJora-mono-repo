export interface Profile { customerUuid: string; programUuid: string; displayName: string; email: string }
export class ApiError extends Error {
  constructor(public status: number, public code: string, public reference?: string) { super(code); }
}
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try { response = await fetch(path, { ...init, credentials: 'same-origin', cache: 'no-store' }); }
  catch { throw new ApiError(503, 'NETWORK_UNAVAILABLE'); }
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new ApiError(response.status, typeof error.code === 'string' ? error.code : 'REQUEST_FAILED',
      typeof error.reference === 'string' ? error.reference : undefined);
  }
  return response.status === 204 ? undefined as T : response.json();
}
async function mutate<T>(method: string, path: string, body?: unknown) {
  const { requestToken } = await request<{ requestToken: string }>('/api/v1/idm/csrf');
  return request<T>(path, { method, headers: { 'X-CSRF-TOKEN': requestToken, 'Content-Type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
}
export const api = {
  program: () => request<{ displayName: string }>('/api/v1/cor/programs/resolve?key=cjora-demo'),
  profile: () => request<Profile>('/api/v1/cor/customers/me'),
  login: (email: string) => mutate<{ authorizationUrl: string }>('POST', '/api/v1/idm/sessions',
    { programKey: 'cjora-demo', email, returnPath: '/app' }),
  logout: () => mutate<void>('DELETE', '/api/v1/idm/sessions/current'),
};
export function messageFor(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong. Please try again.';
  if (error.code === 'REVOCATION_PENDING') return 'You are signed out here. Session cleanup is still being completed. Please try again later.';
  if (error.status === 401) return 'Your session has ended. Please sign in again.';
  if (error.status === 403) return 'Access could not be confirmed. Please sign in again or contact your Program administrator.';
  if (error.status === 429) return 'Too many requests. Please wait a minute and try again.';
  if (error.status === 400) return 'We could not start sign-in. Check your email and try again.';
  return 'We cannot connect right now. Please try again shortly.';
}
