"use client";

import { useCallback, useMemo, useState } from "react";

export function updateHiddenPanelIds<TPanelId extends string>(
  current: ReadonlySet<TPanelId>,
  panelId: TPanelId,
  visible: boolean
) {
  const next = new Set(current);
  if (visible) {
    next.delete(panelId);
  } else {
    next.add(panelId);
  }
  return next;
}

export function usePanelVisibilityState<TPanelId extends string>(
  initialHiddenPanelIds: Iterable<TPanelId> | (() => Iterable<TPanelId>) = []
) {
  const [hiddenPanelIds, setHiddenPanelIds] = useState<ReadonlySet<TPanelId>>(
    () => new Set(
      typeof initialHiddenPanelIds === "function"
        ? initialHiddenPanelIds()
        : initialHiddenPanelIds
    )
  );
  const hiddenPanelIdList = useMemo(() => [...hiddenPanelIds], [hiddenPanelIds]);
  const isPanelVisible = useCallback(
    (panelId: TPanelId) => !hiddenPanelIds.has(panelId),
    [hiddenPanelIds]
  );
  const setPanelVisible = useCallback((panelId: TPanelId, visible: boolean) => {
    setHiddenPanelIds((current) => updateHiddenPanelIds(current, panelId, visible));
  }, []);
  const resetPanelVisibility = useCallback((nextHiddenPanelIds: Iterable<TPanelId> = []) => {
    setHiddenPanelIds(new Set(nextHiddenPanelIds));
  }, []);

  return {
    hiddenPanelIdList,
    hiddenPanelIds,
    isPanelVisible,
    resetPanelVisibility,
    setHiddenPanelIds,
    setPanelVisible,
  };
}
