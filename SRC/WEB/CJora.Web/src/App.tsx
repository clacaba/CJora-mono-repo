import { useEffect, useState, type FormEvent } from 'react';
import { api, ApiError, messageFor, type Profile } from './api';

type State = { kind: 'loading' } | { kind: 'signedOut'; notice?: string } |
  { kind: 'profile'; profile: Profile } | { kind: 'unavailable'; message: string };

export function App() {
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [program, setProgram] = useState('CJora Demo');
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function load() {
    setError(''); setState({ kind: 'loading' });
    try { setState({ kind: 'profile', profile: await api.profile() }); }
    catch (e) {
      if (e instanceof ApiError && (e.status === 401 || e.status === 403))
        setState({ kind: 'signedOut', notice: e.status === 403 ? messageFor(e) : undefined });
      else setState({ kind: 'unavailable', message: messageFor(e) });
    }
  }
  useEffect(() => {
    api.program().then(p => setProgram(p.displayName)).catch(() => {});
    if (window.location.pathname === '/app/login-failed') {
      window.history.replaceState(null, '', '/app');
      setState({ kind: 'signedOut', notice: 'Sign-in was not completed. Please try again.' });
    } else { void load(); }
  }, []);
  async function login(event: FormEvent) {
    event.preventDefault(); setBusy(true); setError('');
    try {
      const result = await api.login(email.trim());
      const destination = new URL(result.authorizationUrl);
      // The server validates the provider origin; this also rejects executable/non-web URLs.
      if (destination.protocol !== 'https:' && !(destination.protocol === 'http:' && destination.hostname === 'localhost'))
        throw new Error('Invalid authorization destination');
      window.location.assign(destination.href);
    } catch (e) { setError(messageFor(e)); setBusy(false); }
  }
  async function logout() {
    setBusy(true); setError('');
    try {
      await api.logout(); setState({ kind: 'signedOut', notice: 'You have signed out of CJora.' }); setEmail('');
    } catch (e) {
      if (e instanceof ApiError && (e.code === 'REVOCATION_PENDING' || e.status === 401)) {
        setState({ kind: 'signedOut', notice: messageFor(e) }); setEmail('');
      } else {
        // Hide customer details until a fresh authoritative check succeeds.
        setState({ kind: 'unavailable', message: messageFor(e) });
      }
    } finally { setBusy(false); }
  }
  return <div className="shell">
    <header><a className="brand" href="/app" aria-label="CJora home"><span className="mark">CJ</span>CJora<span className="brand-dot">.</span></a><span className="program">{program}</span></header>
    <main>
      <aside><span className="eyebrow">YOUR PROGRAM. YOUR NEXT CHAPTER.</span><h1>A clearer view.<br/>A confident next step.</h1><p>A dedicated space for your Program, with secure access to the details that matter.</p><div className="art" aria-hidden="true"><span/><span/><span/><div className="art-label">Built around you.</div></div><div className="demo-note">Fictional demonstration Program</div></aside>
      <section className="panel" aria-label="Your CJora account">
        {state.kind === 'loading' && <div role="status"><div className="spinner"/><h2>Opening your space</h2><p>Checking your session securely.</p></div>}
        {state.kind === 'unavailable' && <><span className="eyebrow">LET'S RECONNECT</span><h2>We couldn't connect</h2><p role="alert">{state.message}</p><button onClick={() => void load()}>Try again</button></>}
        {state.kind === 'signedOut' && <><span className="eyebrow">WELCOME TO CJORA</span><h2>Your space starts here.</h2><p>Sign in to access your Program profile.</p>{state.notice && <div className="notice" role="status">{state.notice}</div>}<form onSubmit={login}><label htmlFor="email">Email address</label><input id="email" type="email" name="email" autoComplete="email" placeholder="you@example.com" required maxLength={254} value={email} onChange={e => setEmail(e.target.value)} disabled={busy}/><button disabled={busy} type="submit">{busy ? 'Opening secure sign-in…' : 'Continue to sign in'}<span aria-hidden="true">↗</span></button></form><p className="small">Continue to your secure sign-in page to verify your identity.</p></>}
        {state.kind === 'profile' && <><div className="profile-top"><span className="eyebrow">YOUR PROGRAM PROFILE</span><span className="pill">Signed in</span></div><div className="avatar" aria-hidden="true">{state.profile.displayName.slice(0,1).toUpperCase()}</div><h2>Hello, {state.profile.displayName}.</h2><p>You're in your CJora space.</p><dl><div><dt>Email address</dt><dd>{state.profile.email}</dd></div><div><dt>Program</dt><dd>{program}</dd></div><div><dt>Customer reference</dt><dd className="reference">{state.profile.customerUuid}</dd></div></dl><button className="secondary" onClick={() => void logout()} disabled={busy}>{busy ? 'Signing out…' : 'Sign out'}</button><p className="small">Sign out when you're finished, especially on a shared device.</p></>}
        {error && <div className="notice error" role="alert">{error}</div>}
      </section>
    </main>
    <footer><span>© {new Date().getFullYear()} CJora</span><span>A space for what comes next.</span></footer>
  </div>;
}
