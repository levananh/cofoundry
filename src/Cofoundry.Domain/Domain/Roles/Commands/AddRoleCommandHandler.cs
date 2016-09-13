﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using System.Data.Entity;
using Cofoundry.Domain.Data;
using Cofoundry.Domain.CQS;
using Cofoundry.Core.Validation;
using Cofoundry.Core;

namespace Cofoundry.Domain
{
    public class AddRoleCommandHandler 
        : IAsyncCommandHandler<AddRoleCommand>
        , IPermissionRestrictedCommandHandler<AddRoleCommand>
    {
        #region constructor

        private readonly CofoundryDbContext _dbContext;
        private readonly IQueryExecutor _queryExecutor;
        private readonly ICommandExecutor _commandExecutor;
        private readonly IPermissionRepository _permissionRepository;
        private readonly IPermissionValidationService _permissionValidationService;
        private readonly IUserAreaRepository _userAreaRepository;

        public AddRoleCommandHandler(
            CofoundryDbContext dbContext,
            IQueryExecutor queryExecutor,
            IPermissionRepository permissionRepository,
            ICommandExecutor commandExecutor,
            IPermissionValidationService permissionValidationService,
            IUserAreaRepository userAreaRepository
            )
        {
            _dbContext = dbContext;
            _queryExecutor = queryExecutor;
            _permissionRepository = permissionRepository;
            _commandExecutor = commandExecutor;
            _permissionValidationService = permissionValidationService;
            _userAreaRepository = userAreaRepository;
        }

        #endregion

        #region execution

        public async Task ExecuteAsync(AddRoleCommand command, IExecutionContext executionContext)
        {
            ValidatePermissions(command);
            var isUnique = await _queryExecutor.ExecuteAsync(GetUniqueQuery(command));
            ValidateIsUnique(isUnique);

            var dbUserArea = await GetOrAddUserArea(command);
            var permissions = await GetCommandPermissions(command);
            var role = MapAndAddRole(command, executionContext, permissions, dbUserArea);
            await _dbContext.SaveChangesAsync();

            command.OutputRoleId = role.RoleId;
        }

        #endregion

        #region helpers

        private async Task<UserArea> GetOrAddUserArea(AddRoleCommand command)
        {
            var userArea = _userAreaRepository.GetByCode(command.UserAreaCode);
            var dbUserArea = await  _dbContext
                .UserAreas
                .SingleOrDefaultAsync(a => a.UserAreaCode == userArea.UserAreaCode);

            if (dbUserArea == null)
            {
                dbUserArea = new UserArea();
                dbUserArea.UserAreaCode = userArea.UserAreaCode;
                dbUserArea.Name = userArea.Name;
                _dbContext.UserAreas.Add(dbUserArea);
            }

            return dbUserArea;
        }

        private Role MapAndAddRole(AddRoleCommand command, IExecutionContext executionContext, List<Permission> permissions, UserArea dbUserArea)
        {
            _permissionValidationService.EnforceHasPermissionToUserArea(command.UserAreaCode, executionContext.UserContext);

            var role = new Role();
            role.Title = command.Title.Trim();
            role.UserArea = dbUserArea;

            foreach (var permission in EnumerableHelper.Enumerate(permissions))
            {
                role.Permissions.Add(permission);
            }
            
            _dbContext.Roles.Add(role);
            return role;
        }

        private void ValidatePermissions(AddRoleCommand command)
        {
            if (!EnumerableHelper.IsNullOrEmpty(command.Permissions))
            {
                var entityWithoutReadPermission = command.Permissions
                    .Where(p => !string.IsNullOrEmpty(p.EntityDefinitionCode))
                    .GroupBy(p => p.EntityDefinitionCode)
                    .Where(g => !g.Any(p => p.PermissionCode == CommonPermissionTypes.ReadPermissionCode));

                foreach (var entity in entityWithoutReadPermission)
                {
                    var entityCode = entity.First().EntityDefinitionCode;
                    var readPermission = _permissionRepository.GetByEntityAndPermissionType(entityCode, CommonPermissionTypes.ReadPermissionCode);

                    if (readPermission != null)
                    {
                        var msg = "Read permissions must be granted to entity " + entityCode + " in order to assign additional permissions";
                        throw new ValidationException(msg);
                    }
                }
            }
        }

        private void ValidateIsUnique(bool isUnique)
        {
            if (!isUnique)
            {
                throw new PropertyValidationException("A role with this title already exists", "Title");
            }
        }

        private IsRoleTitleUniqueQuery GetUniqueQuery(AddRoleCommand command)
        {
            return new IsRoleTitleUniqueQuery()
            {
                Title = command.Title.Trim(),
                UserAreaCode = command.UserAreaCode
            };
        }

        private async Task<List<Permission>> GetCommandPermissions(AddRoleCommand command)
        {
            var permissions = new List<Permission>();

            if (!EnumerableHelper.IsNullOrEmpty(command.Permissions))
            {
                var dbPermissions = await _dbContext
                    .Permissions
                    .ToListAsync();

                foreach (var permissionCommand in command.Permissions)
                {
                    var dbPermission = dbPermissions
                        .SingleOrDefault(p => 
                            p.PermissionCode == permissionCommand.PermissionCode 
                            && (string.IsNullOrWhiteSpace(permissionCommand.EntityDefinitionCode) || p.EntityDefinitionCode == permissionCommand.EntityDefinitionCode)
                            );
                    // Because permissions are defined in code, it might not exists yet. If it doesn't lets add it.
                    if (dbPermission == null)
                    {

                        var codePermission = _permissionRepository.GetByCode(permissionCommand.PermissionCode, permissionCommand.EntityDefinitionCode);
                        dbPermission = new Permission();
                        dbPermission.PermissionCode = codePermission.PermissionType.Code;

                        if (codePermission is IEntityPermission)
                        {
                            var definitionCode = ((IEntityPermission)codePermission).EntityDefinition.EntityDefinitionCode;
                            await _commandExecutor.ExecuteAsync(new EnsureEntityDefinitionExistsCommand(definitionCode));
                            dbPermission.EntityDefinitionCode = definitionCode;
                        }
                    }

                    permissions.Add(dbPermission);
                }
            }

            return permissions;
        }

        #endregion
        
        #region Permission

        public IEnumerable<IPermissionApplication> GetPermissions(AddRoleCommand command)
        {
            yield return new RoleCreatePermission();
        }

        #endregion
    }
}