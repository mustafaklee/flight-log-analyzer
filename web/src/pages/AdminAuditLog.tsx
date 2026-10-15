import { Group, Pagination, Stack, Text, TextInput } from "@mantine/core";
import { useDebouncedValue } from "@mantine/hooks";
import { useState } from "react";
import { useAdminAuditLog } from "../api/queries";
import { EmptyState, ErrorState, LoadingState } from "../components/States";
import { AuditTable } from "../flight/ActivityPanel";

/** The audit log of every team, for administrators; filtered by action (prefix ending with ".") or by record id. */
export function AdminAuditLog() {
    const [action, setAction] = useState("");
    const [entityId, setEntityId] = useState("");
    const [debouncedAction] = useDebouncedValue(action, 300);
    const [page, setPage] = useState(1);
    const id = entityId.trim();
    const log = useAdminAuditLog({
        action: debouncedAction.trim() || undefined,
        entityId: /^[0-9a-f-]{36}$/i.test(id) ? id : undefined,
        page,
        pageSize: 50,
    });

    return (
        <Stack gap="sm">
            <Group align="flex-end">
                <TextInput
                    label="İşlem"
                    placeholder="ör. team. veya data.exported"
                    value={action}
                    onChange={(e) => {
                        setAction(e.currentTarget.value);
                        setPage(1);
                    }}
                    w={240}
                />
                <TextInput
                    label="Kayıt kimliği"
                    value={entityId}
                    onChange={(e) => {
                        setEntityId(e.currentTarget.value);
                        setPage(1);
                    }}
                    w={320}
                />
                {log.data && (
                    <Text size="xs" c="dimmed">
                        {log.data.totalCount} kayıt
                    </Text>
                )}
            </Group>
            {log.isPending ? (
                <LoadingState />
            ) : log.isError ? (
                <ErrorState error={log.error} onRetry={() => log.refetch()} />
            ) : log.data.items.length === 0 ? (
                <EmptyState title="Kayıt yok" />
            ) : (
                <>
                    <AuditTable entries={log.data.items} showFlight />
                    {log.data.totalPages > 1 && (
                        <Pagination
                            size="sm"
                            total={log.data.totalPages}
                            value={page}
                            onChange={setPage}
                        />
                    )}
                </>
            )}
        </Stack>
    );
}
