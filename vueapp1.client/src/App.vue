<script setup lang="ts"></script>

<template>
  <header>
    <nav>
      <RouterLink to="/"> Home </RouterLink>
      <RouterLink to="/weather"> Weather </RouterLink>
    </nav>
  </header>

  <main>
    <RouterView />
  </main>
</template>

<style scoped>
header {
  border-bottom: 1px solid var(--color-border);
  margin-bottom: 2rem;
  padding-bottom: 1rem;
}

nav {
  display: flex;
  gap: 1.5rem;
}

nav a {
  font-weight: 500;
}

nav a.router-link-active {
  color: var(--color-heading);
}
</style>
