import { MantineProvider } from '@mantine/core';
import { ModalsProvider } from '@mantine/modals';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { lazy, Suspense, type ReactNode } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router';
import { ApiError } from './api/client';
import { AuthProvider, useAuth } from './auth/AuthContext';
import { TeamProvider, useTeam } from './auth/TeamContext';
import { Layout } from './components/Layout';
import { NoTeam } from './components/NoTeam';
import { EmptyState, LoadingState } from './components/States';
import { AircraftPage } from './pages/AircraftPage';
import { AuditLogPage } from './pages/AuditLogPage';
import { DashboardPage } from './pages/DashboardPage';
import { FleetPage } from './pages/FleetPage';
import { FlightsPage } from './pages/FlightsPage';
import { LoginPage } from './pages/LoginPage';
import { ProjectsPage } from './pages/ProjectsPage';
import { ReviewsPage } from './pages/ReviewsPage';
import { TeamPage } from './pages/TeamPage';
import { UploadPage } from './pages/UploadPage';
import { theme } from './theme';

// The flight page carries the chart and map libraries; load it on first use.
const FlightDetailPage = lazy(() => import('./pages/FlightDetailPage').then((m) => ({ default: m.FlightDetailPage })));
const ComparePage = lazy(() => import('./pages/ComparePage').then((m) => ({ default: m.ComparePage })));
const AdminPage = lazy(() => import('./pages/AdminPage').then((m) => ({ default: m.AdminPage })));

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        refetchOnWindowFocus: false,
        // Client errors (403, 404, validation) do not get better by retrying.
        retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
      },
    },
  });
}

function RequireAuth({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  const location = useLocation();
  if (status === 'restoring') {
    return <LoadingState label="Oturum açılıyor…" />;
  }

  if (status === 'signedOut') {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }

  return children;
}

function RequireTeam({ children }: { children: ReactNode }) {
  const { team } = useTeam();
  return team ? children : <NoTeam />;
}

function RequireAdmin({ children }: { children: ReactNode }) {
  const { isAdministrator } = useAuth();
  return isAdministrator ? children : <EmptyState title="Yetki yok">Bu sayfa yalnızca yöneticilere açıktır.</EmptyState>;
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <RequireAuth>
            <Layout />
          </RequireAuth>
        }
      >
        <Route index element={<RequireTeam><DashboardPage /></RequireTeam>} />
        <Route path="flights" element={<RequireTeam><FlightsPage /></RequireTeam>} />
        <Route path="flights/:flightId" element={<Suspense fallback={<LoadingState />}><FlightDetailPage /></Suspense>} />
        <Route path="compare" element={<Suspense fallback={<LoadingState />}><ComparePage /></Suspense>} />
        <Route path="reviews" element={<RequireTeam><ReviewsPage /></RequireTeam>} />
        <Route path="audit-log" element={<RequireTeam><AuditLogPage /></RequireTeam>} />
        <Route path="upload" element={<RequireTeam><UploadPage /></RequireTeam>} />
        <Route path="fleet" element={<RequireTeam><FleetPage /></RequireTeam>} />
        <Route path="fleet/:aircraftId" element={<AircraftPage />} />
        <Route path="projects" element={<RequireTeam><ProjectsPage /></RequireTeam>} />
        <Route path="team" element={<RequireTeam><TeamPage /></RequireTeam>} />
        <Route path="admin" element={<RequireAdmin><Suspense fallback={<LoadingState />}><AdminPage /></Suspense></RequireAdmin>} />
        <Route path="*" element={<EmptyState title="Sayfa bulunamadı">Adres yanlış ya da sayfa kaldırılmış.</EmptyState>} />
      </Route>
    </Routes>
  );
}

export function Providers({ children, client }: { children: ReactNode; client: QueryClient }) {
  return (
    <MantineProvider theme={theme} defaultColorScheme="auto">
      <ModalsProvider>
        <Notifications position="top-right" />
        <QueryClientProvider client={client}>
          <AuthProvider>
            <TeamProvider>{children}</TeamProvider>
          </AuthProvider>
        </QueryClientProvider>
      </ModalsProvider>
    </MantineProvider>
  );
}

const queryClient = createQueryClient();

export function App() {
  return (
    <Providers client={queryClient}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </Providers>
  );
}
