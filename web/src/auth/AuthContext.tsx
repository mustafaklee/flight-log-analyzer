import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { api, session } from '../api/client';
import type { AccessTokenResponse, UserProfile } from '../api/types';

interface AuthState {
  /** null while the stored session is being restored. */
  user: UserProfile | null;
  status: 'restoring' | 'signedIn' | 'signedOut';
  isAdministrator: boolean;
  login(email: string, password: string): Promise<void>;
  register(email: string, password: string, displayName: string): Promise<void>;
  logout(): void;
  reloadProfile(): Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [user, setUser] = useState<UserProfile | null>(null);
  const [status, setStatus] = useState<AuthState['status']>(session.hasRefreshToken ? 'restoring' : 'signedOut');

  const reloadProfile = useCallback(async () => {
    const me = await api<UserProfile>('/auth/me');
    setUser(me);
    setStatus('signedIn');
  }, []);

  useEffect(() => {
    if (status === 'restoring') {
      let cancelled = false;
      api<UserProfile>('/auth/me').then(
        (me) => {
          if (!cancelled) {
            setUser(me);
            setStatus('signedIn');
          }
        },
        () => !cancelled && setStatus('signedOut'),
      );
      return () => {
        cancelled = true;
      };
    }
  }, [status]);

  useEffect(
    () =>
      session.subscribe((signedIn) => {
        if (!signedIn) {
          setUser(null);
          setStatus('signedOut');
          queryClient.clear();
        }
      }),
    [queryClient],
  );

  const login = useCallback(
    async (email: string, password: string) => {
      const tokens = await api<AccessTokenResponse>('/auth/login', { method: 'POST', body: { email, password }, anonymous: true });
      session.store(tokens);
      await reloadProfile();
    },
    [reloadProfile],
  );

  const register = useCallback(
    async (email: string, password: string, displayName: string) => {
      await api('/auth/register', { method: 'POST', body: { email, password, displayName }, anonymous: true });
      await login(email, password);
    },
    [login],
  );

  const logout = useCallback(() => session.clear(), []);

  const value = useMemo<AuthState>(
    () => ({
      user,
      status,
      isAdministrator: user?.roles.includes('Administrator') ?? false,
      login,
      register,
      logout,
      reloadProfile,
    }),
    [user, status, login, register, logout, reloadProfile],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const value = useContext(AuthContext);
  if (!value) {
    throw new Error('useAuth must be used inside AuthProvider');
  }

  return value;
}
