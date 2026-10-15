import { Alert, Anchor, Button, Card, Center, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core';
import { useState } from 'react';
import { Navigate, useLocation } from 'react-router';
import { ApiError, errorMessage } from '../api/client';
import { useAuth } from '../auth/AuthContext';

export function LoginPage() {
  const { login, register, status } = useAuth();
  const location = useLocation();
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (status === 'signedIn') {
    const from = (location.state as { from?: string } | null)?.from ?? '/';
    return <Navigate to={from} replace />;
  }

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      if (mode === 'login') {
        await login(email, password);
      } else {
        await register(email, password, displayName);
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        setError('E-posta veya parola hatalı.');
      } else if (err instanceof ApiError && err.status === 429) {
        setError('Çok fazla deneme yapıldı. Bir dakika sonra tekrar deneyin.');
      } else if (err instanceof ApiError && err.status === 403 && mode === 'register') {
        setError('Kayıt bu sunucuda kapalı. Bir yöneticiden hesap isteyin.');
      } else {
        setError(errorMessage(err));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <Center mih="100vh" p="md">
      <Card withBorder padding="xl" w={400} maw="100%">
        <form onSubmit={submit}>
          <Stack>
            <Stack gap={4}>
              <Title order={2}>FlightLog Intelligence</Title>
              <Text c="dimmed" size="sm">
                {mode === 'login' ? 'Uçuş loglarınıza erişmek için giriş yapın.' : 'Yeni hesap oluşturun.'}
              </Text>
            </Stack>
            {error && (
              <Alert color="red" role="alert">
                {error}
              </Alert>
            )}
            {mode === 'register' && (
              <TextInput label="Ad soyad" required value={displayName} onChange={(e) => setDisplayName(e.currentTarget.value)} autoComplete="name" />
            )}
            <TextInput label="E-posta" type="email" required value={email} onChange={(e) => setEmail(e.currentTarget.value)} autoComplete="email" />
            <PasswordInput
              label="Parola"
              required
              value={password}
              onChange={(e) => setPassword(e.currentTarget.value)}
              autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
              description={mode === 'register' ? 'En az 10 karakter; büyük harf, küçük harf ve rakam içermeli.' : undefined}
            />
            <Button type="submit" loading={busy} fullWidth>
              {mode === 'login' ? 'Giriş yap' : 'Hesap oluştur'}
            </Button>
            <Text size="sm" ta="center">
              {mode === 'login' ? 'Hesabınız yok mu? ' : 'Hesabınız var mı? '}
              <Anchor component="button" type="button" onClick={() => { setMode(mode === 'login' ? 'register' : 'login'); setError(null); }}>
                {mode === 'login' ? 'Kayıt olun' : 'Giriş yapın'}
              </Anchor>
            </Text>
          </Stack>
        </form>
      </Card>
    </Center>
  );
}
