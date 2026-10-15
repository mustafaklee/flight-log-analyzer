import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppRoutes } from './App';
import { mockApi, renderWithApp } from './test/render';

afterEach(() => vi.unstubAllGlobals());

describe('routing', () => {
  it('sends a signed-out user to the login form', async () => {
    mockApi(() => undefined);
    renderWithApp(<AppRoutes />, '/flights');
    expect(await screen.findByRole('button', { name: 'Giriş yap' })).toBeInTheDocument();
  });

  it('restores the session from the stored refresh token and offers team creation when the user has no team', async () => {
    localStorage.setItem('flightlog.refreshToken', 'stored');
    mockApi((url) => {
      if (url.pathname === '/api/v1/auth/refresh') return { body: { tokenType: 'Bearer', accessToken: 'a', expiresIn: 3600, refreshToken: 'r' } };
      if (url.pathname === '/api/v1/auth/me') return { body: { id: 'u1', email: 'a@b.c', displayName: 'Ayşe', roles: [], teams: [] } };
      return undefined;
    });
    renderWithApp(<AppRoutes />, '/');
    expect(await screen.findByText('Henüz bir ekibiniz yok')).toBeInTheDocument();
  });
});
