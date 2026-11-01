// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest';
import { api, ApiError } from './api';
afterEach(()=>vi.unstubAllGlobals());
it('fetches a fresh CSRF token before each logout and sends same-origin credentials',async()=>{
 const fetch=vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({requestToken:'one'})))
  .mockResolvedValueOnce(new Response(null,{status:204})).mockResolvedValueOnce(new Response(JSON.stringify({requestToken:'two'})))
  .mockResolvedValueOnce(new Response(null,{status:204}));
 vi.stubGlobal('fetch',fetch);await api.logout();await api.logout();
 expect(fetch.mock.calls[1][1]).toMatchObject({method:'DELETE',credentials:'same-origin',headers:{'X-CSRF-TOKEN':'one'}});
 expect(fetch.mock.calls[3][1]).toMatchObject({headers:{'X-CSRF-TOKEN':'two'}});
});
it('retains revocation-pending classification without provider details',async()=>{
 vi.stubGlobal('fetch',vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({requestToken:'token'})))
  .mockResolvedValueOnce(new Response(JSON.stringify({code:'REVOCATION_PENDING'}),{status:503})));
 await expect(api.logout()).rejects.toMatchObject({status:503,code:'REVOCATION_PENDING'});
});
it('turns network failures into safe dependency failures',async()=>{
 vi.stubGlobal('fetch',vi.fn().mockRejectedValue(new Error('private network details')));
 await expect(api.profile()).rejects.toEqual(new ApiError(503,'NETWORK_UNAVAILABLE'));
});
