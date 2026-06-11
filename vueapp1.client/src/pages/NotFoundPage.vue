<template>
  <div class="not-found">
    <h1>Page not found</h1>
    <p>The page you are looking for doesn't exist or has moved.</p>
    <RouterLink to="/"> Go to the home page </RouterLink>
  </div>
</template>

<style scoped>
.not-found {
  text-align: center;
  padding-top: 4rem;
}

h1 {
  font-size: 2rem;
  font-weight: 600;
  margin-bottom: 0.5rem;
}

p {
  margin-bottom: 2rem;
}
</style>
