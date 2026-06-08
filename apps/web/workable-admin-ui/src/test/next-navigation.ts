type NextNavigationMockState = {
  refreshCount: number;
  replaces: string[];
};

type GlobalWithNavigationState = typeof globalThis & {
  __workableAdminUiNextNavigationMock?: NextNavigationMockState;
};

const mutableGlobal = globalThis as GlobalWithNavigationState;

function createEmptyState(): NextNavigationMockState {
  return {
    refreshCount: 0,
    replaces: [],
  };
}

function getState() {
  mutableGlobal.__workableAdminUiNextNavigationMock ??= createEmptyState();
  return mutableGlobal.__workableAdminUiNextNavigationMock;
}

export function resetNextNavigationMock() {
  mutableGlobal.__workableAdminUiNextNavigationMock = createEmptyState();
}

export function getNextNavigationRouterCalls() {
  const state = getState();
  return {
    refreshCount: state.refreshCount,
    replaces: [...state.replaces],
  };
}
