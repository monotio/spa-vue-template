<script setup lang="ts"></script>

<template>
  <div class="home">
    <h1>Welcome</h1>
    <p>Your app is running. Edit this page to get started.</p>

    <div class="links">
      <RouterLink to="/weather"> Weather demo </RouterLink>
      <a href="/scalar/v1" target="_blank" rel="noopener">API docs</a>
    </div>
  </div>
</template>

<style scoped>
.home {
  text-align: center;
  padding-top: 4rem;
}

h1 {
  font-size: 2rem;
  font-weight: 600;
  margin-bottom: 0.5rem;
}

.links {
  display: flex;
  gap: 1.5rem;
  justify-content: center;
  margin-top: 2rem;
}
</style>
