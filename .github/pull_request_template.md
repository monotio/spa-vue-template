## Summary
- 

## Checklist
- [ ] Architecture impact reviewed (contracts, layering, and extension points)
- [ ] Frontend: `npm run lint:check`, `npm run type-check`, `npm --prefix vueapp1.client run test:coverage`
- [ ] Backend: `dotnet test /p:CollectCoverage=true /p:Threshold=80 /p:ThresholdType=line /p:ThresholdStat=total`
- [ ] API contract: `npm run openapi:check`
- [ ] No breaking changes to public API contracts unless explicitly documented
- [ ] Documentation updated (`README.md`, `CLAUDE.md`, or `docs/*`) when behavior changed

## Risk
- **Risk level**: low | medium | high
- **Rollback plan**: 
