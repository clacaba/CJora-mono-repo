// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { App } from './App';
import { api, ApiError } from './api';
vi.mock('./api', async original => ({ ...await original<typeof import('./api')>(), api: { program: vi.fn(), profile: vi.fn(), login: vi.fn(), logout: vi.fn() } }));
beforeEach(() => {
  vi.resetAllMocks();
  window.history.replaceState(null,'','/app');
  vi.mocked(api.program).mockResolvedValue({ displayName: 'Demo Program' });
  vi.mocked(api.profile).mockRejectedValue(new ApiError(401,'SESSION_REQUIRED'));
});
afterEach(cleanup);
it('offers email sign-in without collecting a password', async () => {
  render(<App/>);
  expect(await screen.findByRole('button',{name:/continue to sign in/i})).toBeEnabled();
  expect(screen.getByLabelText('Email address')).toHaveAttribute('type','email');
  expect(document.querySelector('input[type=password]')).toBeNull();
});
it('shows only the authorized profile and signs out', async () => {
  vi.mocked(api.profile).mockResolvedValue({customerUuid:'customer-1',programUuid:'program-1',displayName:'Alex',email:'alex@example.test'});
  vi.mocked(api.logout).mockResolvedValue(undefined);
  render(<App/>);
  expect(await screen.findByRole('heading',{name:'Hello, Alex.'})).toBeVisible();
  await userEvent.click(screen.getByRole('button',{name:'Sign out'}));
  expect(await screen.findByText('You have signed out of CJora.')).toBeVisible();
  expect(screen.queryByText('alex@example.test')).toBeNull();
});
it('hides profile details after uncertain logout', async () => {
  vi.mocked(api.profile).mockResolvedValue({customerUuid:'customer-1',programUuid:'program-1',displayName:'Alex',email:'alex@example.test'});
  vi.mocked(api.logout).mockRejectedValue(new ApiError(503,'NETWORK_UNAVAILABLE'));
  render(<App/>);
  await userEvent.click(await screen.findByRole('button',{name:'Sign out'}));
  expect(await screen.findByRole('button',{name:'Try again'})).toBeVisible();
  expect(screen.queryByText('alex@example.test')).toBeNull();
});
it('shows the signed-out state for queued revocation', async () => {
  vi.mocked(api.profile).mockResolvedValue({customerUuid:'customer-1',programUuid:'program-1',displayName:'Alex',email:'alex@example.test'});
  vi.mocked(api.logout).mockRejectedValue(new ApiError(503,'REVOCATION_PENDING'));
  render(<App/>);
  await userEvent.click(await screen.findByRole('button',{name:'Sign out'}));
  expect(await screen.findByText(/session cleanup is still being completed/i)).toBeVisible();
  expect(screen.queryByText('alex@example.test')).toBeNull();
});
it('denied membership shows sign-in without customer data', async () => {
  vi.mocked(api.profile).mockRejectedValue(new ApiError(403,'MEMBERSHIP_DENIED'));
  render(<App/>);
  expect(await screen.findByText(/contact your Program administrator/i)).toBeVisible();
  expect(screen.queryByRole('button',{name:'Sign out'})).toBeNull();
});
it('provider failure becomes a safe sign-in screen and clears the URL', async () => {
  window.history.replaceState(null,'','/app/login-failed?code=discard');
  render(<App/>);
  expect(await screen.findByText('Sign-in was not completed. Please try again.')).toBeVisible();
  expect(window.location.pathname+window.location.search).toBe('/app');
  expect(api.profile).not.toHaveBeenCalled();
});
it('shows recoverable errors without raw exception text', async () => {
  vi.mocked(api.login).mockRejectedValue(new Error('private diagnostic'));
  render(<App/>);
  await userEvent.type(await screen.findByLabelText('Email address'),'alex@example.test');
  await userEvent.click(screen.getByRole('button',{name:/continue to sign in/i}));
  expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong. Please try again.');
  expect(api.login).toHaveBeenCalledWith('alex@example.test');
});
