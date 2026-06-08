export function useRouter() {
  return {
    refresh() {
      getState().refreshCount += 1;
    },
    replace(href) {
      getState().replaces.push(String(href));
    },
  };
}

const stateKey = "__workableAdminUiNextNavigationMock";

function getState() {
  globalThis[stateKey] ??= {
    refreshCount: 0,
    replaces: [],
  };

  return globalThis[stateKey];
}
